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
  /** A revision the server rejected as invalid: not re-sent until the next change. */
  private rejectedRevision = -1;
  /** The last content taken while the editor was alive: a save that runs after the editor was destroyed sends it. */
  private snapshot: JSONContent | null = null;
  private snapshotRevision = -1;
  private editor: Editor | null = null;
  /** Finds the editor that is on screen now (TipTap puts it on its root element). */
  private locate: () => Editor | null = () => null;
  /** Valid signatures the first edit would outdate (read by the focus handler). */
  signaturesToOutdate = 0;

  constructor(private settings: Settings) {
    this.rowVersion = settings.content.rowVersion;
  }

  /** The live editor (a destroyed instance — React StrictMode recreates editors — never replaces a live one). */
  attach(editor: Editor | null): void {
    if (editor && !editor.isDestroyed) {
      this.editor = editor;
    }
  }

  /** How to find the editor on screen; preferred over remembered instances. */
  locateWith(locate: () => Editor | null): void {
    this.locate = locate;
  }

  private live(): Editor | null {
    const found = this.locate();
    if (found && !found.isDestroyed) {
      this.editor = found;
    }

    return this.editor && !this.editor.isDestroyed ? this.editor : null;
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

  /** The content to send: the live editor's, else (destroyed on unmount) the snapshot taken before. */
  private body(): JSONContent | null {
    const editor = this.live();
    if (editor) {
      this.snapshot = toSchema(editor.getJSON(), this.settings.schema);
      this.snapshotRevision = this.revision;
    }

    return this.snapshotRevision === this.revision ? this.snapshot : null;
  }

  /**
   * The server's content changed without us (another window, a script; refetched on focus or remount): shown if there
   * are no local changes, so nobody reads — or signs — text that is no longer there. With local changes the next save
   * reports the conflict.
   */
  external(content: NodeContentView): void {
    if (content.rowVersion === this.rowVersion || this.dirty || this.running) {
      return;
    }

    this.rowVersion = content.rowVersion;
    this.apply(content.contentJson);
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
    const editor = this.live();
    if (!editor) {
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
    if (!this.dirty || this.blocked || this.revision === this.rejectedRevision) {
      return;
    }

    if (this.running) {
      this.body(); // take the content now: the editor may be destroyed when the running save returns
      await this.running;
      return this.save();
    }

    const body = this.body();
    if (!body) {
      return;
    }

    this.running = this.send(body);
    try {
      await this.running;
    } finally {
      this.running = null;
    }

    if (this.dirty && !this.blocked && this.revision !== this.rejectedRevision) {
      this.schedule();
    }
  }

  private async send(body: JSONContent): Promise<void> {
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
      const editor = this.live();
      if (this.revision === sent && editor && !sameContent(editor.getJSON(), saved.contentJson, schema)) {
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
        this.rejectedRevision = sent; // re-sent only after the next change
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

  /** The section goes away: pending changes are saved from a snapshot (the editor itself is destroyed right after). */
  dispose(): void {
    if (this.timer) {
      clearTimeout(this.timer);
    }

    this.body();
    void this.save();
  }
}
