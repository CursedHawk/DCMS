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
    public static string Generate(string database = "", string plugins = "") => $$"""
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

        /** Who may call it over HTTP, GET or POST /api/{app}/fn/{name}. Without it, it is not callable over HTTP. */
        export interface Access {
          http?: Array<"public" | "user" | "apiKey" | `role:${string}`>;
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
          messages: { emit(topic: string, payload?: Json): Promise<void> };
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

        """;

    private static string Indent(string members) =>
        string.Join("\n", members.Split('\n').Select(line => line.Length == 0 ? line : "  " + line));
}
