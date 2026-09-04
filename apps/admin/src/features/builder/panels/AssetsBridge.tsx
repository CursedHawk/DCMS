import type { Editor } from 'grapesjs';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@dcms/admin-ui';
import { MediaPicker } from '../../media/MediaPicker';
import { assetIdFrom, mediaUrlFor } from './TraitsPanel';
import { useCustomPayload } from './useEditorEvent';

/**
 * Replaces GrapesJS's built-in asset manager with the DCMS media library.
 *
 * Double-clicking an image in the canvas opens the Asset Manager; running it in
 * `custom: true` mode means that request arrives here as an event carrying a
 * `select` callback, so the author gets the real library — with its folders,
 * uploads and quota — instead of a second, parallel asset store that nothing
 * else in the platform knows about.
 */

interface AssetsCustomData {
  open: boolean;
  types: string[];
  close: () => void;
  select: (asset: { src: string } | string, complete?: boolean) => void;
}

export function AssetsBridge({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  const payload = useCustomPayload<AssetsCustomData>(editor, 'asset:custom');
  const [open, setOpen] = useState(false);

  // GrapesJS drives the open state; mirroring it into React state is what lets
  // the dialog be closed from either side without them disagreeing.
  useEffect(() => {
    setOpen(payload?.open ?? false);
  }, [payload]);

  if (!payload) return null;

  const close = () => {
    setOpen(false);
    payload.close();
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) close();
      }}
    >
      <DialogContent className="max-w-3xl">
        <DialogHeader>
          <DialogTitle>{t('builder.chooseAsset')}</DialogTitle>
        </DialogHeader>
        <MediaPicker
          value={undefined}
          category="Image"
          onChange={(id) => {
            if (!id) return;
            // `complete: true` tells GrapesJS the choice is final, which closes
            // its own flow and applies the src to the target component.
            payload.select({ src: mediaUrlFor(id) }, true);
            close();
          }}
        />
      </DialogContent>
    </Dialog>
  );
}

export { assetIdFrom, mediaUrlFor };
