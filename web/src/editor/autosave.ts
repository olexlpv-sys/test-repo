import type { Editor, JSONContent } from '@tiptap/core';
import type { QueryClient } from '@tanstack/react-query';
import { api, ApiError, unwrap } from '../api/client';
import { keys, type NodeContentView } from '../document/api';
import { fromSchema, sameContent, toSchema, type ContentSchemaInfo } from './contentSchema';

export type SaveStatus =
  | { kind: 'idle' }
  | { kind: 'unsaved' }
  | { kind: 'saving' }
  | { kind: 'saved'; at: Date }
  | { kind: 'conflict' }
  | { kind: 'invalid'; message: string }
  | { kind: 'error'; message: string };

export const AutosaveDelay = 1500;

interface Settings {
  documentId: number;
  content: NodeContentView;
  schema: ContentSchemaInfo;
  queryClient: QueryClient;
  onStatus: (status: SaveStatus) => void;
  onConflict: () => void;
}

/**
 * Debounced autosave of one section (T15 §3): 1.5 s after the last change, on blur and on unmount; one save at a time with
 * the latest row version; the canonical JSON goes back into the editor only if it differs and nothing was typed meanwhile.
 * Plain object (not React state): editor callbacks and timers always see the current values.
 */
export class Autosave {
  private rowVersion: string;
  private revision = 0;
  private savedRevision = 0;
  private running: Promise<void> | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private applyingRemote = false;
  private blocked = false;
  private editor: Editor | null = null;
  /** Valid signatures the first edit would outdate (read by the focus handler). */
  signaturesToOutdate = 0;

  constructor(private settings: Settings) {
    this.rowVersion = settings.content.rowVersion;
  }

  attach(editor: Editor | null): void {
    this.editor = editor;
  }

  warnAbout(signatures: number): void {
    this.signaturesToOutdate = signatures;
  }

  configure(settings: Omit<Settings, 'content'>): void {
    this.settings = { ...this.settings, ...settings };
  }

  get dirty(): boolean {
    return this.revision !== this.savedRevision;
  }

  /** A local change in `editor` (ignored while the editor is being set from the server). */
  changed(editor: Editor): void {
    if (this.applyingRemote) {
      return;
    }

    this.editor = editor;
    this.revision++;
    this.settings.onStatus({ kind: 'unsaved' });
    this.schedule();
  }

  schedule(): void {
    if (this.timer) {
      clearTimeout(this.timer);
    }

    this.timer = setTimeout(() => void this.save(), AutosaveDelay);
  }

  async flush(): Promise<void> {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }

    await this.save();
  }

  /** Sets the editor from the server without counting it as a local change. */
  apply(json: unknown): void {
    const editor = this.editor;
    if (!editor || editor.isDestroyed) {
      return;
    }

    this.applyingRemote = true;
    try {
      editor.commands.setContent(fromSchema(json), { emitUpdate: false });
    } finally {
      this.applyingRemote = false;
    }
  }

  async save(): Promise<void> {
    const editor = this.editor;
    if (!editor || editor.isDestroyed || !this.dirty || this.blocked) {
      return;
    }

    if (this.running) {
      await this.running;
      return this.save();
    }

    this.running = this.send(editor, toSchema(editor.getJSON(), this.settings.schema));
    try {
      await this.running;
    } finally {
      this.running = null;
    }

    if (this.dirty && !this.blocked) {
      this.schedule();
    }
  }

  private async send(editor: Editor, body: JSONContent): Promise<void> {
    const { documentId, content, schema, queryClient, onStatus } = this.settings;
    const sent = this.revision;
    onStatus({ kind: 'saving' });
    try {
      const saved = await unwrap(
        api.PUT('/api/nodes/{nodeId}/content', {
          params: { path: { nodeId: content.nodeId } },
          body: { contentJson: body as never, rowVersion: this.rowVersion },
        }),
      );
      this.rowVersion = saved.rowVersion;
      this.savedRevision = Math.max(this.savedRevision, sent);
      queryClient.setQueryData(keys.content(documentId, content.nodeId), saved);
      if (this.revision === sent && !editor.isDestroyed && !sameContent(editor.getJSON(), saved.contentJson, schema)) {
        this.apply(saved.contentJson);
      }

      onStatus(this.dirty ? { kind: 'unsaved' } : { kind: 'saved', at: new Date() });
      // Change badges, history and signature states depend on the content.
      for (const key of [
        ['doc', documentId, 'summary'],
        ['doc', documentId, 'signatures'],
        ['doc', documentId, 'node-history', content.logicalNodeId],
        ['doc', documentId, 'changes', content.logicalNodeId],
      ]) {
        void queryClient.invalidateQueries({ queryKey: key });
      }
    } catch (error) {
      if (error instanceof ApiError && error.type === 'concurrency-conflict') {
        this.blocked = true; // until the user decides
        onStatus({ kind: 'conflict' });
        this.settings.onConflict();
      } else if (error instanceof ApiError && error.errors) {
        const [path, messages] = Object.entries(error.errors)[0] ?? ['', []];
        onStatus({ kind: 'invalid', message: `${messages.join(' ')}${path ? ` (${path})` : ''}` });
      } else {
        onStatus({ kind: 'error', message: error instanceof Error ? error.message : String(error) });
      }
    }
  }

  /** Conflict resolution: keep the server's text, or overwrite it with the local one. */
  async resolve(mode: 'theirs' | 'overwrite'): Promise<void> {
    const { documentId, content, queryClient, onStatus } = this.settings;
    const theirs = await unwrap(
      api.GET('/api/nodes/{nodeId}/content', { params: { path: { nodeId: content.nodeId } } }),
    );
    this.rowVersion = theirs.rowVersion;
    this.blocked = false;
    queryClient.setQueryData(keys.content(documentId, content.nodeId), theirs);
    if (mode === 'theirs') {
      this.apply(theirs.contentJson);
      this.savedRevision = this.revision;
      onStatus({ kind: 'saved', at: new Date() });
    } else {
      await this.save();
    }
  }

  dispose(): void {
    if (this.timer) {
      clearTimeout(this.timer);
    }

    void this.save();
  }
}
