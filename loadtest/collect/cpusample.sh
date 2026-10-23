#!/usr/bin/env bash
# Every 5 s until killed: "<epoch> <container> <cpu-seconds>" per running container, from its cgroup,
# and "<epoch> HOST <busy cpu-seconds>" from /proc/stat (everything but idle and iowait, kernel and
# softirq included). Exact counters, so a 45 s window needs no Prometheus extrapolation -- cAdvisor
# is scraped every 30 s and node CPU every 60 s, which is coarser than the windows routecost measures.
#
#   ssh vps1 'bash -s' < loadtest/collect/cpusample.sh > <run>/cpusample.txt
declare -A NAME
while :; do
    t=$(date +%s)
    read -r _ user nice system idle iowait rest < /proc/stat
    busy=$(( user + nice + system ))
    for v in $rest; do busy=$(( busy + v )); done
    echo "$t HOST $busy" | awk '{ printf "%s %s %.2f\n", $1, $2, $3 / 100 }'
    for d in /sys/fs/cgroup/system.slice/docker-*.scope; do
        id=${d##*/docker-}; id=${id%.scope}
        [ -n "${NAME[$id]:-}" ] || NAME[$id]=$(docker inspect -f '{{.Name}}' "$id" 2>/dev/null | tr -d /)
        u=$(awk '/^usage_usec/ { print $2 }' "$d/cpu.stat" 2>/dev/null) || continue
        [ -n "$u" ] && echo "$t ${NAME[$id]} $(awk -v u="$u" 'BEGIN { printf "%.3f", u / 1e6 }')"
    done
    sleep 5
done
