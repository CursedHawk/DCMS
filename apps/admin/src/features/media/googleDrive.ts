import { useQuery } from '@tanstack/react-query';
import { ApiError, api } from '../../lib/api';

/**
 * Google Drive as an upload source: Google's own picker in the browser, the download on the
 * server. The picker hands back a short-lived `drive.file` token and the ids the admin chose;
 * admin-api fetches each file and puts it through the same ingest as an upload, so a Drive
 * image gets the same webp ladder. Nothing here is stored — the token lives in memory until
 * it expires, only so a second import does not ask to sign in again.
 */

export interface DriveConfig {
  clientId: string;
  apiKey: string;
  appId: string;
}

export interface DriveFile {
  id: string;
  name: string;
  sizeBytes: number;
}

/** null = Drive is not configured on this platform (the server says 404); the button hides. */
export function useDriveConfig() {
  return useQuery({
    queryKey: ['google-drive-config'],
    queryFn: async () => {
      try {
        return await api.get<DriveConfig>('/admin/media/google-drive/config');
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) return null;
        throw e;
      }
    },
    staleTime: Infinity,
    retry: false,
  });
}

/** Server-side import of one picked file; same response as an upload. */
export const importDriveFile = (accessToken: string, fileId: string, folderId?: string | null) =>
  api.post<{ id: string }>('/admin/media/google-drive/import', {
    accessToken,
    fileId,
    folderId: folderId ?? null,
  });

/**
 * What the library can store, plus the Google-native types the server exports (Docs, Sheets,
 * Slides → PDF; Drawings → PNG). Filtering here means an admin never picks a file the server
 * would refuse as an unsupported type.
 */
const PICKABLE_MIME_TYPES = [
  'image/jpeg',
  'image/png',
  'image/gif',
  'image/webp',
  'image/svg+xml',
  'video/mp4',
  'audio/mpeg',
  'audio/ogg',
  'audio/wav',
  'audio/x-wav',
  'application/pdf',
  'application/zip',
  'application/vnd.google-apps.document',
  'application/vnd.google-apps.spreadsheet',
  'application/vnd.google-apps.presentation',
  'application/vnd.google-apps.drawing',
].join(',');

const SCOPE = 'https://www.googleapis.com/auth/drive.file';

// ---- Google's scripts, typed only as far as this file uses them --------------------------

interface TokenResponse {
  access_token?: string;
  expires_in?: number;
  error?: string;
}
interface PickerDoc {
  id: string;
  name: string;
  sizeBytes?: number;
}
interface PickerResponse {
  action: string;
  docs?: PickerDoc[];
}
interface PickerBuilder {
  setAppId(v: string): PickerBuilder;
  setDeveloperKey(v: string): PickerBuilder;
  setOAuthToken(v: string): PickerBuilder;
  setLocale(v: string): PickerBuilder;
  setOrigin(v: string): PickerBuilder;
  setMaxItems(v: number): PickerBuilder;
  addView(v: unknown): PickerBuilder;
  enableFeature(f: string): PickerBuilder;
  setCallback(cb: (r: PickerResponse) => void): PickerBuilder;
  build(): { setVisible(v: boolean): void; dispose(): void };
}
interface GoogleGlobal {
  accounts: {
    oauth2: {
      initTokenClient(c: {
        client_id: string;
        scope: string;
        callback: (r: TokenResponse) => void;
        error_callback: (e: { type: string }) => void;
      }): { requestAccessToken(o?: { prompt?: string }): void };
    };
  };
  picker: {
    PickerBuilder: new () => PickerBuilder;
    DocsView: new (viewId?: string) => {
      setMimeTypes(v: string): unknown;
      setIncludeFolders(v: boolean): unknown;
      setSelectFolderEnabled(v: boolean): unknown;
      setEnableDrives(v: boolean): unknown;
    };
    ViewId: { DOCS: string };
    Feature: { MULTISELECT_ENABLED: string; SUPPORT_DRIVES: string };
    Action: { PICKED: string; CANCEL: string };
  };
}
declare global {
  interface Window {
    google?: GoogleGlobal;
    gapi?: { load(name: string, cb: () => void): void };
  }
}

const scripts = new Map<string, Promise<void>>();

/** One `<script>` per URL for the life of the page, however many uploaders mount. */
function loadScript(src: string): Promise<void> {
  let p = scripts.get(src);
  if (!p) {
    p = new Promise<void>((resolve, reject) => {
      const el = document.createElement('script');
      el.src = src;
      el.async = true;
      el.onload = () => resolve();
      el.onerror = () => {
        scripts.delete(src); // let the next click retry rather than fail forever
        reject(new Error(`Could not load ${src}`));
      };
      document.head.appendChild(el);
    });
    scripts.set(src, p);
  }
  return p;
}

let pickerReady: Promise<void> | undefined;

/**
 * Fetch both Google scripts ahead of the click. The sign-in popup has to open close to the
 * click that asked for it or the browser blocks it, so the scripts must already be here.
 */
export function preloadDrive(): Promise<void> {
  pickerReady ??= Promise.all([
    loadScript('https://accounts.google.com/gsi/client'),
    loadScript('https://apis.google.com/js/api.js').then(
      () => new Promise<void>((resolve) => window.gapi!.load('picker', resolve)),
    ),
  ]).then(
    () => undefined,
    (e: unknown) => {
      pickerReady = undefined;
      throw e;
    },
  );
  return pickerReady;
}

let cachedToken: { value: string; expiresAt: number } | undefined;

/** Thrown when the admin closes the sign-in popup or the picker: not an error to report. */
export class DriveCancelled extends Error {}

function accessToken(clientId: string): Promise<string> {
  // A minute of margin: the server still has to use it after the picker closes.
  if (cachedToken && cachedToken.expiresAt > Date.now() + 60_000)
    return Promise.resolve(cachedToken.value);
  return new Promise((resolve, reject) => {
    window
      .google!.accounts.oauth2.initTokenClient({
        client_id: clientId,
        scope: SCOPE,
        callback: (r) => {
          if (!r.access_token) return reject(new Error(r.error ?? 'Google sign-in failed.'));
          cachedToken = {
            value: r.access_token,
            expiresAt: Date.now() + (r.expires_in ?? 3600) * 1000,
          };
          resolve(r.access_token);
        },
        error_callback: (e) =>
          reject(
            e.type === 'popup_closed'
              ? new DriveCancelled()
              : new Error(`Google sign-in failed (${e.type}).`),
          ),
      })
      .requestAccessToken({ prompt: '' });
  });
}

/** The server said the token is no good; the next pick signs in again. */
export function forgetDriveToken() {
  cachedToken = undefined;
}

/**
 * Sign in if needed, show Google's picker, resolve with what was picked (empty on cancel) and
 * the token the server needs to fetch it.
 */
export async function pickFromDrive(
  config: DriveConfig,
  { multiple, locale }: { multiple: boolean; locale: string },
): Promise<{ token: string; files: DriveFile[] }> {
  await preloadDrive();
  const token = await accessToken(config.clientId);
  const g = window.google!.picker;

  const view = new g.DocsView(g.ViewId.DOCS);
  view.setMimeTypes(PICKABLE_MIME_TYPES);
  // Folders to browse into, not to pick: the server imports files.
  view.setIncludeFolders(true);
  view.setSelectFolderEnabled(false);
  view.setEnableDrives(true);

  return new Promise((resolve) => {
    const releaseDialogs = shieldFromDialogs();
    let builder = new g.PickerBuilder()
      .setAppId(config.appId)
      .setDeveloperKey(config.apiKey)
      .setOAuthToken(token)
      .setOrigin(window.location.origin)
      .setLocale(locale)
      .addView(view)
      .enableFeature(g.Feature.SUPPORT_DRIVES);
    if (multiple) builder = builder.enableFeature(g.Feature.MULTISELECT_ENABLED).setMaxItems(50);

    const picker = builder
      .setCallback((r) => {
        if (r.action !== g.Action.PICKED && r.action !== g.Action.CANCEL) return; // 'loaded'
        picker.dispose();
        releaseDialogs();
        resolve({
          token,
          files: (r.docs ?? []).map((d) => ({
            id: d.id,
            name: d.name,
            sizeBytes: d.sizeBytes ?? 0,
          })),
        });
      })
      .build();
    picker.setVisible(true);
  });
}

const PICKER = '.picker-dialog, .picker-dialog-bg';

/**
 * The picker is appended to <body>, outside any open Radix dialog — and the uploader lives in
 * three of them (media pickers, the builder's asset manager). A modal dialog would treat every
 * click on the picker as "outside" and close, and its focus trap would pull focus back out of
 * the picker's iframe, so the search box could not be typed in. These events are stopped at the
 * window, before the dialog's document listeners see them, for as long as the picker is open.
 * (Pointer events, which the modal turns off on <body>, are restored for it in index.css.)
 */
function shieldFromDialogs(): () => void {
  const shield = (e: Event) => {
    const target = e.type === 'focusout' ? (e as FocusEvent).relatedTarget : e.target;
    if (target instanceof Element && target.closest(PICKER)) e.stopImmediatePropagation();
  };
  const types = ['focusin', 'focusout', 'pointerdown'] as const;
  for (const type of types) window.addEventListener(type, shield, true);
  return () => {
    for (const type of types) window.removeEventListener(type, shield, true);
  };
}
