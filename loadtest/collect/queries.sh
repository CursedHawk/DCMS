# The PromQL the evidence bundle is made of, as `name|expr` lines.
#
# One file so a dashboard panel and a load-test finding cannot quietly disagree about how
# a number is defined. Read by collect.sh; not executable on its own.
#
# Shape of the set, and why each group is here:
#
#   latency_*      Per ROUTE, not just per service. The recording rules in
#                  infra/observability/prometheus/rules/recording.yml aggregate
#                  dcms:http_latency:p95 by service only, which answers "admin-api got
#                  slower" and not "which endpoint". The endpoint is the finding.
#   saturation_*   Whether a service is out of CPU or merely serialized. CFS throttling is
#                  the one that distinguishes them: a container at 100% of its quota with
#                  throttled time is starved, one at 40% with a growing queue is blocked.
#   dotnet_*       Thread-pool queue length is the clearest signal of async starvation, and
#                  allocation rate is what makes a buffering handler visible as GC pressure.
#   pipeline_*     The workers. A request path can look perfectly healthy while the queue
#                  behind it grows without bound, and that is a bottleneck too.
#   store_*        Postgres and Redis. A cache that is not hitting and a pool that is
#                  exhausted both present as "the service is slow".
#
# EVERY RANGE HERE IS [2m] OR WIDER, AND THAT IS NOT A STYLE CHOICE. The application
# metrics arrive over OTLP at the SDK's default 60-SECOND export interval (measured: 10
# samples per 600s). rate() needs two samples inside its window, so rate(x[1m]) over a
# 60s-spaced series is reliably EMPTY -- it returns no error, just no data, which in a
# bundle is indistinguishable from an idle platform.
#
# The platform's own `dcms:http_requests:rate1m` recording rule has this bug today and is
# permanently empty; six Grafana panels read it. See the findings for this run.

QUERIES=$(cat <<'PROMQL'
latency_p95_by_route|histogram_quantile(0.95, sum by (service, http_route, le) (rate(http_server_request_duration_seconds_bucket{http_route!~"/health.*|/metrics"}[2m])))
latency_p99_by_route|histogram_quantile(0.99, sum by (service, http_route, le) (rate(http_server_request_duration_seconds_bucket{http_route!~"/health.*|/metrics"}[2m])))
latency_p95_by_service|dcms:http_latency:p95
requests_by_route|dcms:http_requests:by_route:rate5m
request_rate|dcms:http_requests:rate1m
error_ratio|dcms:http_error_ratio:rate5m
status_codes|sum by (service, http_response_status_code) (rate(http_server_request_duration_seconds_count{http_route!~"/health.*|/metrics"}[2m]))
saturation_cpu|sum by (name) (rate(container_cpu_usage_seconds_total{name!=""}[2m]))
saturation_memory|container_memory_working_set_bytes{name!=""}
saturation_cpu_throttled|sum by (name) (rate(container_cpu_cfs_throttled_seconds_total{name!=""}[2m]))
saturation_host_cpu|1 - avg(rate(node_cpu_seconds_total{mode="idle"}[2m]))
saturation_host_memory|dcms:host_memory_used_ratio
saturation_host_load|node_load1
dotnet_threadpool_queue|dotnet_thread_pool_queue_length_total
dotnet_threadpool_threads|dotnet_thread_pool_thread_count_total
dotnet_alloc_rate|rate(dotnet_gc_heap_allocated_bytes_total[2m])
dotnet_gc_pause|rate(dotnet_gc_pause_time_seconds_total[2m])
dotnet_exceptions|rate(dotnet_exceptions_total[2m])
dotnet_lock_contention|rate(dotnet_monitor_lock_contentions_total[2m])
kestrel_queued_connections|kestrel_queued_connections
kestrel_active_connections|kestrel_active_connections
pipeline_outbox_depth|dcms_outbox_depth
pipeline_outbox_lag|histogram_quantile(0.95, sum by (outbox, le) (rate(dcms_outbox_lag_seconds_bucket[2m])))
pipeline_message_duration|histogram_quantile(0.95, sum by (stream, subject, le) (rate(dcms_messages_handling_duration_seconds_bucket[2m])))
pipeline_messages_consumed|sum by (stream, subject, outcome) (rate(dcms_messages_consumed_total[2m]))
pipeline_media_duration|histogram_quantile(0.95, sum by (kind, le) (rate(dcms_media_process_duration_seconds_bucket[5m])))
pipeline_media_processed|sum by (kind, outcome) (rate(dcms_media_processed_total[5m]))
pipeline_site_build_duration|histogram_quantile(0.95, sum by (mode, le) (rate(dcms_site_build_duration_seconds_bucket[5m])))
pipeline_jetstream_pending|nats_consumer_num_pending
pipeline_jetstream_ack_pending|nats_consumer_num_ack_pending
pipeline_jetstream_redelivered|nats_consumer_num_redelivered
store_pg_connections|sum(pg_stat_activity_count)
store_pg_max_connections|pg_settings_max_connections
store_pg_cache_hit|rate(pg_stat_database_blks_hit[2m]) / clamp_min(rate(pg_stat_database_blks_hit[2m]) + rate(pg_stat_database_blks_read[2m]), 0.000001)
store_pg_deadlocks|rate(pg_stat_database_deadlocks[5m])
store_redis_hit_ratio|rate(redis_keyspace_hits_total[2m]) / clamp_min(rate(redis_keyspace_hits_total[2m]) + rate(redis_keyspace_misses_total[2m]), 0.000001)
store_redis_clients|redis_connected_clients
store_redis_ops|rate(redis_commands_processed_total[2m])
store_redis_blocked|redis_blocked_clients
PROMQL
)
