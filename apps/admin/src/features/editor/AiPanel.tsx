import { parseSiteDefinition } from '@dcms/editor-core';
import { useMutation } from '@tanstack/react-query';
import { Sparkles, Wand2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '../../components/ui/dialog';
import { Button } from '../../components/ui/button';
import { Label } from '../../components/ui/label';
import { Textarea } from '../../components/ui/input';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/tabs';
import { api } from '../../lib/api';
import { useEditor } from './store';

interface GenResult {
  valid: boolean;
  result?: unknown;
  raw?: string;
}

export function AiPanel({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) {
  const { t } = useTranslation();
  const addGeneratedNode = useEditor((s) => s.addGeneratedNode);
  const replaceDefinition = useEditor((s) => s.replaceDefinition);
  const [instruction, setInstruction] = useState('');
  const [brief, setBrief] = useState('');

  const genComponent = useMutation({
    mutationFn: () =>
      api.post<GenResult>('/admin/ai/generate/component', { instruction: instruction.trim() }),
    onSuccess: (r) => {
      if (r.valid && r.result && typeof r.result === 'object') {
        try {
          addGeneratedNode(r.result as never);
          toast.success(t('common.saved'));
          onOpenChange(false);
        } catch {
          toast.error(t('errors.generic'));
        }
      } else {
        toast.error(t('errors.generic'));
      }
    },
    onError: () => toast.error(t('errors.generic')),
  });

  const genSite = useMutation({
    mutationFn: () => api.post<GenResult>('/admin/ai/generate/site', { brief: brief.trim() }),
    onSuccess: (r) => {
      if (r.valid && r.result) {
        try {
          const def = parseSiteDefinition(r.result);
          replaceDefinition(def);
          toast.success(t('common.saved'));
          onOpenChange(false);
        } catch {
          toast.error(t('errors.generic'));
        }
      } else {
        toast.error(t('errors.generic'));
      }
    },
    onError: () => toast.error(t('errors.generic')),
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent wide>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Sparkles className="h-5 w-5 text-primary" /> {t('editor.aiAssist')}
          </DialogTitle>
        </DialogHeader>
        <Tabs defaultValue="component">
          <TabsList>
            <TabsTrigger value="component">{t('editor.generateComponent')}</TabsTrigger>
            <TabsTrigger value="site">{t('editor.generateSite')}</TabsTrigger>
          </TabsList>
          <TabsContent value="component" className="space-y-3">
            <Label>{t('editor.instruction')}</Label>
            <Textarea
              rows={4}
              value={instruction}
              onChange={(e) => setInstruction(e.target.value)}
              placeholder="A hero with a headline and a call-to-action button"
            />
            <Button disabled={!instruction.trim() || genComponent.isPending} onClick={() => genComponent.mutate()}>
              <Wand2 className="h-4 w-4" /> {t('editor.generateComponent')}
            </Button>
          </TabsContent>
          <TabsContent value="site" className="space-y-3">
            <Label>{t('editor.brief')}</Label>
            <Textarea
              rows={5}
              value={brief}
              onChange={(e) => setBrief(e.target.value)}
              placeholder="A landing page for a coffee shop with menu, gallery and contact"
            />
            <p className="text-xs text-muted-foreground">
              {t('editor.generateSite')} — replaces the current draft (never auto-published).
            </p>
            <Button disabled={!brief.trim() || genSite.isPending} onClick={() => genSite.mutate()}>
              <Wand2 className="h-4 w-4" /> {t('editor.generateSite')}
            </Button>
          </TabsContent>
        </Tabs>
      </DialogContent>
    </Dialog>
  );
}
