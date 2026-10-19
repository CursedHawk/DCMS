import type { CodeIsland as CodeIslandType } from '@dcms/site-runtime';
import { useEffect, useRef, useState } from 'react';
import { useVisual } from '../store';

/** The tallest an island may make itself: a component that grows forever must not stretch the canvas. */
const MAX_HEIGHT = 4000;

/**
 * A developer component on the canvas: a frame of the island page (islands.ts), told which
 * component and props to draw.
 *
 * <p>The frame is `sandbox="allow-scripts"` and nothing more — an opaque origin, so the code in
 * it can touch neither the canvas nor the admin. Its messages are believed only from this frame
 * (`event.source`), and only for what they may say: that it is ready, how tall it is, that it
 * failed. It takes no pointer events, so selecting and dragging still land on the node around it,
 * as for any other component.</p>
 *
 * <p>Until the island page is built, and whenever the component fails, the placeholder stands
 * in.</p>
 */
export const CodeIsland: CodeIslandType = ({ contract, props, fallback }) => {
  const srcdoc = useVisual((s) => s.island.srcdoc);
  const frame = useRef<HTMLIFrameElement>(null);
  const [ready, setReady] = useState(false);
  const [height, setHeight] = useState(0);
  const [failed, setFailed] = useState<string | null>(null);
  const propsJson = JSON.stringify(props ?? {});

  // A new page is a new frame: it says it is ready again, and may draw what failed before.
  useEffect(() => {
    setReady(false);
    setFailed(null);
  }, [srcdoc]);

  useEffect(() => {
    const element = frame.current;
    const host = element?.ownerDocument.defaultView;
    if (!element || !host) return;
    const onMessage = (event: MessageEvent) => {
      if (event.source !== element.contentWindow) return;
      const data = event.data as { __dcms?: unknown; kind?: unknown; height?: unknown; message?: unknown } | null;
      if (data?.__dcms !== 'island') return;
      if (data.kind === 'ready') setReady(true);
      else if (data.kind === 'size' && typeof data.height === 'number' && Number.isFinite(data.height)) setHeight(Math.min(Math.max(Math.round(data.height), 0), MAX_HEIGHT));
      else if (data.kind === 'error') setFailed(String(data.message ?? 'The component failed.').slice(0, 300));
    };
    host.addEventListener('message', onMessage);
    return () => host.removeEventListener('message', onMessage);
  }, [srcdoc]);

  // Props travel as JSON: what the canvas holds is data, and the island must not receive anything
  // that is not.
  useEffect(() => {
    if (!ready) return;
    frame.current?.contentWindow?.postMessage({ __dcms: 'island-draw', name: contract.name, props: JSON.parse(propsJson) as unknown }, '*');
  }, [ready, propsJson, contract.name]);

  if (!srcdoc) return <>{fallback}</>;
  const shown = ready && !failed;
  return (
    <div className="dcms-code-island" data-dcms-island={contract.name}>
      {shown ? null : fallback}
      {failed ? (
        <p className="dcms-editor-note" role="status">
          {contract.label}: {failed}
        </p>
      ) : null}
      <iframe
        ref={frame}
        title={contract.label}
        sandbox="allow-scripts"
        srcDoc={srcdoc}
        style={{
          display: 'block',
          width: '100%',
          border: 0,
          background: 'transparent',
          // Hidden, not removed, while it starts: it must load to say it is ready.
          height: shown ? height : 0,
          visibility: shown ? 'visible' : 'hidden',
          pointerEvents: 'none',
        }}
      />
    </div>
  );
};
