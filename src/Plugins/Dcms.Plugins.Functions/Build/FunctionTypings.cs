namespace Dcms.Plugins.Functions.Build;

/// <summary>
/// <c>@dcms/functions</c> as TypeScript declarations (ADR 0029): what a function is checked
/// against at build time and what the editor offers. The runtime behind it is the static
/// <c>sdk-runtime.js</c> in script-runner; these describe it. The workspace-specific parts —
/// the Dynamic Apps tables, other apps' contracts — are generated into <see cref="Database"/> and
/// <see cref="Plugins"/>.
/// </summary>
public static class FunctionTypings
{
    /// <param name="database">The members of <c>Database</c>; empty until the workspace's tables are generated in.</param>
    /// <param name="plugins">The members of <c>Plugins</c>, likewise.</param>
    /// <param name="declarations">Top-level declarations those members use: each table's record interface.</param>
    public static string Generate(string database = "", string plugins = "", string declarations = "") => $$"""
        // @dcms/functions — generated for this workspace's environment. Do not edit: it is rebuilt.

        export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

        /** Where a function is triggered from. Build them with `on`. */
        export interface Trigger { readonly kind: string }

        export declare const on: {
          /** A Dynamic Apps table's records, as "app.table": the app's address, then the table's API name. */
          row(table: string): {
            created(options?: Record<string, never>): Trigger;
            /** `fields`: only when one of these changed. */
            updated(options?: { fields?: string[] }): Trigger;
            deleted(options?: Record<string, never>): Trigger;
          };
          relation(relationship: string): { created(): Trigger; deleted(): Trigger };
          /** A message another function emitted, or one posted to the app's /fn/_messages/{topic}. */
          message(topic: string): Trigger;
          /** A cron expression (5 fields, UTC), at most every 5 minutes. */
          schedule(cron: string): Trigger;
          /** An interval such as "15m", "1h" or "1d", between 5 minutes and 7 days. */
          every(interval: string): Trigger;
          event(name: "form.submitted" | "visitor.registered" | "user.invited" | "user.activated"): Trigger;
        };

        /**
         * Who may call it over HTTP, GET or POST /api/{app}/fn/{name}. Without it, it is not callable over HTTP.
         * "public": anyone. "user": anyone signed in to the site. "role": site users a role grants
         * "invoke" on this function (Users → Roles). "apiKey": callers presenting one of this app's keys in X-Api-Key.
         */
        export interface Access {
          http?: Array<"public" | "user" | "role" | "apiKey">;
        }

        /** Everything the function may reach. Whatever is not here is refused at run time. */
        export interface Uses {
          /** "app.table" → what it may do with its records. */
          tables?: Record<string, Array<"read" | "create" | "update" | "delete">>;
          /** Other apps' contracts it calls, as "name@major". */
          contracts?: string[];
          /** Host names outbound HTTP may go to, e.g. "api.stripe.com" or "*.example.com". */
          egress?: string[];
          /** Names of this app's secrets it reads. */
          secrets?: string[];
          messages?: { emit?: string[] };
        }

        export interface Limits {
          /** Wall time, at most the trigger's own (30 s over HTTP, 5 min otherwise). */
          timeoutMs?: number;
          /** 32–256. */
          heapMb?: number;
        }

        export interface Request {
          method: "GET" | "POST";
          path: string;
          query: Record<string, string>;
          headers: Record<string, string>;
          body: Json | string | null;
        }

        export interface User {
          id: string;
          email?: string;
          groups?: string[];
          roles?: string[];
        }

        export interface HttpResponse {
          status?: number;
          headers?: Record<string, string>;
          body?: Json | string;
        }

        /** A condition on records: a field (or one lookup away, "company.name") compared, or conditions combined. */
        export type Filter<R> =
          | { field: (keyof R & string) | `${string}.${string}`; op: "eq" | "ne" | "gt" | "gte" | "lt" | "lte" | "in" | "nin" | "contains" | "startsWith" | "isNull"; value?: Json }
          | { and: Filter<R>[] }
          | { or: Filter<R>[] }
          | { not: Filter<R> };

        export interface Query<R> {
          filter?: Filter<R>;
          sort?: Array<{ field: keyof R & string; direction?: "asc" | "desc" }>;
          /** Free text over the table's searchable fields. */
          search?: string;
          /** The fields to return; id and version always come back. */
          select?: Array<keyof R & string>;
          /** Lookups to return as the record they point at; the target table must be in uses.tables too. */
          expand?: string[];
          page?: number;
          /** At most 200. */
          pageSize?: number;
        }

        export interface Page<R> {
          items: R[];
          /** Counted up to 5,000; totalCapped says when it stopped. */
          total: number;
          page: number;
          pageSize: number;
          totalCapped: boolean;
        }

        type Values<R> = Partial<Omit<R, "id" | "version" | "created_at" | "updated_at" | "created_by" | "updated_by">>;

        /** One table's records. Each method needs its verb in uses.tables: read, create, update or delete. */
        export interface Table<R> {
          find(query?: Query<R>): Promise<Page<R>>;
          get(id: string, options?: { expand?: string[] }): Promise<R | null>;
          count(filter?: Filter<R>): Promise<number>;
          create(values: Values<R>): Promise<R>;
          /** Only the fields to change; pass `version` to refuse a write over someone else's. Null when there is no such record. */
          update(id: string, values: Values<R> & { version?: number }): Promise<R | null>;
          /** False when there was no such record. */
          delete(id: string): Promise<boolean>;
          /** Many-to-many: false unless both records exist. Needs update. */
          link(id: string, relationship: string, targetId: string): Promise<boolean>;
          unlink(id: string, relationship: string, targetId: string): Promise<boolean>;
          /** Up to 500 records. */
          bulkUpdate(ids: string[], values: Values<R>): Promise<{ affected: number }>;
          bulkDelete(ids: string[]): Promise<{ affected: number }>;
        }

        {{declarations}}
        export interface Database {
        {{Indent(database)}}
        }

        export interface Plugins {
        {{Indent(plugins)}}
        }

        export interface Context {
          /** This environment's Dynamic Apps tables, as declared in `uses.tables`. */
          db: Database;
          /** Other apps' contracts, as declared in `uses.contracts`. */
          plugins: Plugins;
          functions: { invoke<O = Json>(name: string, input?: Json): Promise<O> };
          /** Runs a function of this deployment later, on the workspace's jobs page; the same dedupeKey is one job. */
          jobs: { enqueue(fn: string, payload?: Json, options?: { dedupeKey?: string }): Promise<{ id: string }> };
          /** To every function on the topic in this environment, as declared in uses.messages.emit. */
          messages: { emit(topic: string, payload?: Json): Promise<{ started: number }> };
          secrets: { get(name: string): Promise<string | null> };
          /** fetch, to the hosts in `uses.egress` only. */
          http: { fetch(url: string | URL, init?: { method?: string; headers?: Record<string, string>; body?: string | Json }): Promise<Response> };
          log: Pick<Console, "log" | "info" | "debug" | "warn" | "error">;
          /** The HTTP call that started this run; null for any other trigger. */
          request: Request | null;
          /** The signed-in caller, over HTTP; null otherwise. */
          user: User | null;
          /** What started this run: the trigger and its event. */
          trigger: { kind: string; detail: Json } | null;
          run: { function: string; deployment: number; depth: number } | null;
        }

        export interface FunctionSpec<I, O> {
          description?: string;
          triggers?: Trigger[];
          access?: Access;
          uses?: Uses;
          limits?: Limits;
          handler(input: I, ctx: Context): O | Promise<O>;
        }

        export declare function defineFunction<I = Json, O = unknown>(spec: FunctionSpec<I, O>): FunctionSpec<I, O>;

        /** An HTTP answer of your own: return respond(201, body, headers). Any other return value is sent as JSON with 200. */
        export declare function respond(status: number, body?: Json | string, headers?: Record<string, string>): HttpResponse;

        """;

    private static string Indent(string members) =>
        string.Join("\n", members.Split('\n').Select(line => line.Length == 0 ? line : "  " + line));
}
