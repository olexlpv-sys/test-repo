import { afterEach, describe, expect, it, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import type { Editor, JSONContent } from '@tiptap/core';
import { fakeApi, Reply } from '../test/fakeApi';
import { Autosave, AutosaveDelay, type SaveStatus } from './autosave';
import type { ContentSchemaInfo } from './contentSchema';

const schema: ContentSchemaInfo = {
  version: 1,
  fontFamilies: [],
  nodes: {
    doc: { attributes: {}, children: ['paragraph'] },
    paragraph: { attributes: {}, children: ['text'] },
    text: { attributes: {}, children: null },
  },
  marks: { bold: { attributes: {} } },
};
const doc = (text: string): JSONContent => ({
  type: 'doc',
  content: [{ type: 'paragraph', content: [{ type: 'text', text }] }],
});
const view = (text: string, rowVersion: string, json: JSONContent = doc(text)) => ({
  nodeId: 5,
  logicalNodeId: 'l5',
  schemaVersion: 1,
  contentJson: json,
  contentHtml: '',
  modifiedAt: '2026-09-02T10:00:00Z',
  modifiedBy: { id: 2, displayName: 'Alice' },
  rowVersion,
});

/** A stand-in for the TipTap editor: holds JSON, records server-side replacements. */
function stubEditor(initial: string) {
  let json = doc(initial);
  const setContent = vi.fn((next: JSONContent) => {
    json = next;
    return true;
  });
  const editor = { isDestroyed: false, getJSON: () => json, commands: { setContent } } as unknown as Editor;
  return { editor, setContent, type: (text: string) => (json = doc(text)) };
}

function setup(initial = 'Start.') {
  const statuses: SaveStatus[] = [];
  const onConflict = vi.fn();
  const queryClient = new QueryClient();
  const autosave = new Autosave({
    documentId: 7,
    content: view(initial, 'r1'),
    schema,
    queryClient,
    onStatus: (s) => statuses.push(s),
    onConflict,
  });
  const stub = stubEditor(initial);
  return { autosave, statuses, onConflict, queryClient, ...stub };
}

afterEach(() => {
  vi.useRealTimers();
});

describe('section autosave', () => {
  it('saves 1.5 s after the last change, with the latest row version each time', async () => {
    vi.useFakeTimers();
    const versions: string[] = [];
    let row = 1;
    fakeApi({
      'PUT /api/nodes/5/content': (r) => {
        versions.push((r.body as { rowVersion: string }).rowVersion);
        row++;
        return view('', `r${row}`, (r.body as { contentJson: JSONContent }).contentJson);
      },
    });
    const { autosave, editor, type, statuses } = setup();
    type('Start. One.');
    autosave.changed(editor);
    await vi.advanceTimersByTimeAsync(AutosaveDelay - 100);
    type('Start. One. Two.');
    autosave.changed(editor); // typing again restarts the delay
    await vi.advanceTimersByTimeAsync(AutosaveDelay - 100);
    expect(versions).toEqual([]);
    await vi.advanceTimersByTimeAsync(200);
    await vi.waitFor(() => expect(versions).toEqual(['r1']));

    type('Start. One. Two. Three.');
    autosave.changed(editor);
    await vi.advanceTimersByTimeAsync(AutosaveDelay + 10);
    await vi.waitFor(() => expect(versions).toEqual(['r1', 'r2']));
    expect(statuses.at(-1)?.kind).toBe('saved');
  });

  it('puts the canonical JSON back only when it differs and nothing was typed meanwhile', async () => {
    fakeApi({ 'PUT /api/nodes/5/content': () => view('Canonical.', 'r2') });
    const { autosave, editor, type, setContent } = setup();
    type('Typed.');
    autosave.changed(editor);
    await autosave.flush();
    expect(setContent).toHaveBeenCalledTimes(1);
    expect(setContent.mock.calls[0]?.[0]).toEqual(doc('Canonical.'));

    // Unchanged canonical form: the editor is left alone (no caret jump).
    fakeApi({
      'PUT /api/nodes/5/content': (r) => view('', 'r3', (r.body as { contentJson: JSONContent }).contentJson),
    });
    type('Same.');
    autosave.changed(editor);
    await autosave.flush();
    expect(setContent).toHaveBeenCalledTimes(1);
  });

  it('stops on a conflict until the user chooses; "Reload theirs" takes the server text, "Overwrite" saves with the new row version', async () => {
    let conflict = true;
    const sent: string[] = [];
    fakeApi({
      'PUT /api/nodes/5/content': (r) => {
        sent.push((r.body as { rowVersion: string }).rowVersion);
        return conflict
          ? new Reply(409, { type: 'concurrency-conflict', title: 'Conflict' })
          : view('', 'r9', (r.body as { contentJson: JSONContent }).contentJson);
      },
      'GET /api/nodes/5/content': () => view('Theirs.', 'r8'),
    });
    const { autosave, editor, type, onConflict, statuses, setContent } = setup();
    type('Mine.');
    autosave.changed(editor);
    await autosave.flush();
    expect(onConflict).toHaveBeenCalledTimes(1);
    expect(statuses.at(-1)?.kind).toBe('conflict');

    type('Mine, more.');
    autosave.changed(editor);
    await autosave.flush(); // blocked: no second request while the dialog is open
    expect(sent).toEqual(['r1']);

    conflict = false;
    await autosave.resolve('overwrite');
    expect(sent).toEqual(['r1', 'r8']);
    expect(statuses.at(-1)?.kind).toBe('saved');

    conflict = true;
    type('Mine again.');
    autosave.changed(editor);
    await autosave.flush();
    await autosave.resolve('theirs');
    expect(setContent.mock.calls.at(-1)?.[0]).toEqual(doc('Theirs.'));
    expect(autosave.dirty).toBe(false);
  });

  it('reports the offending element of a validation error', async () => {
    fakeApi({
      'PUT /api/nodes/5/content': () =>
        new Reply(400, {
          type: 'validation-failed',
          title: 'Invalid',
          errors: { 'contentJson.content[0].attrs.color': ['Not a #RRGGBB color.'] },
        }),
    });
    const { autosave, editor, type, statuses } = setup();
    type('Bad.');
    autosave.changed(editor);
    await autosave.flush();
    expect(statuses.at(-1)).toEqual({
      kind: 'invalid',
      message: 'Not a #RRGGBB color. (contentJson.content[0].attrs.color)',
    });
  });

  it('saves pending changes when the section goes away, and nothing when there are none', async () => {
    const { calls } = fakeApi({
      'PUT /api/nodes/5/content': (r) => view('', 'r2', (r.body as { contentJson: JSONContent }).contentJson),
    });
    const clean = setup();
    clean.autosave.attach(clean.editor);
    clean.autosave.dispose();
    const dirty = setup();
    dirty.type('Unsaved.');
    dirty.autosave.changed(dirty.editor);
    dirty.autosave.dispose();
    await vi.waitFor(() => expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(1));
  });
});
