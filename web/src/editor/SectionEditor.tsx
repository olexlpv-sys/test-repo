import { useEffect, useState } from 'react';
import { Button, Group, Modal, Text } from '@mantine/core';
import { modals } from '@mantine/modals';
import { EditorContent, useEditor } from '@tiptap/react';
import type { Editor, Extensions, JSONContent } from '@tiptap/core';
import { useQueryClient } from '@tanstack/react-query';
import type { NodeContentView } from '../document/api';
import { fromSchema, type ContentSchemaInfo } from './contentSchema';
import { Autosave, type SaveStatus } from './autosave';
import { useEditorHub, type EditorHub } from './editorHub';

export type { SaveStatus } from './autosave';

interface Props {
  documentId: number;
  versionId: number;
  content: NodeContentView;
  editable: boolean;
  schema: ContentSchemaInfo;
  extensions: Extensions;
  /** Valid signatures the first edit would outdate (a warning before editing a draft that already has signatures). */
  signaturesToOutdate: number;
  /** Content put into the editor as a normal edit ("Restore this text"); applied once per new object. */
  replacement?: JSONContent | null;
  onStatus?: (status: SaveStatus) => void;
}

function warnBeforeEditing(editor: Editor, hub: EditorHub, versionId: number, signatures: number) {
  if (!editor.isEditable || signatures === 0 || hub.warned.has(versionId)) {
    return;
  }

  hub.warned.add(versionId);
  modals.openConfirmModal({
    title: 'This draft already has signatures',
    children: (
      <Text size="sm">
        Editing will outdate {signatures} signature{signatures === 1 ? '' : 's'}; the approvers must sign again.
      </Text>
    ),
    labels: { confirm: 'Edit anyway', cancel: 'Cancel' },
    groupProps: { grow: true },
    onCancel: () => {
      hub.warned.delete(versionId);
      editor.commands.blur();
    },
  });
}

/** One section's text (one `NodeContent`), bound to TipTap with autosave and the conflict dialog (T15 §3). */
export function SectionEditor(props: Props) {
  const { documentId, versionId, content, editable, schema, extensions, signaturesToOutdate, replacement, onStatus } =
    props;
  const queryClient = useQueryClient();
  const hub = useEditorHub();
  const [conflict, setConflict] = useState(false);
  const [status, setStatus] = useState<SaveStatus>({ kind: 'idle' });
  const [autosave] = useState(
    () =>
      new Autosave({
        documentId,
        content,
        schema,
        queryClient,
        onStatus: () => {},
        onConflict: () => {},
      }),
  );

  useEffect(() => {
    autosave.configure({
      documentId,
      schema,
      queryClient,
      onStatus: (next) => {
        setStatus(next);
        onStatus?.(next);
      },
      onConflict: () => setConflict(true),
    });
  }, [autosave, documentId, schema, queryClient, onStatus]);

  useEffect(() => autosave.warnAbout(signaturesToOutdate), [autosave, signaturesToOutdate]);

  const editor = useEditor({
    extensions,
    content: fromSchema(content.contentJson),
    editable,
    immediatelyRender: true,
    shouldRerenderOnTransaction: false,
    editorProps: { attributes: { class: 'dh-section-text', 'aria-label': 'Section text' } },
    onCreate: ({ editor: created }) => {
      autosave.attach(created);
    },
    // The editor comes with each event: the instance that really has the text (StrictMode may recreate it).
    onUpdate: ({ editor: changed }) => autosave.changed(changed),
    onFocus: ({ editor: focused }) => {
      hub.setActive(focused);
      warnBeforeEditing(focused, hub, versionId, autosave.signaturesToOutdate);
    },
    onBlur: ({ editor: blurred }) => {
      autosave.attach(blurred);
      void autosave.flush();
    },
  });

  useEffect(() => {
    autosave.attach(editor);
  }, [autosave, editor]);

  // Unmounting (scrolled out of the virtualized page, navigation): unsaved changes are saved.
  useEffect(() => () => autosave.dispose(), [autosave]);

  useEffect(() => {
    if (editor.isEditable !== editable) {
      editor.setEditable(editable, false); // not an edit: no update event
    }
  }, [editor, editable]);

  useEffect(() => {
    if (replacement) {
      editor.commands.setContent(replacement, { emitUpdate: true });
      editor.commands.focus('end');
    }
  }, [editor, replacement]);

  return (
    <>
      <EditorContent editor={editor} data-status={status.kind} />
      <Modal
        opened={conflict}
        onClose={() => setConflict(false)}
        title="Changed by someone else"
        closeOnClickOutside={false}
      >
        <Text size="sm">
          This section was saved by someone else after you opened it. Keep their text, or overwrite it with yours?
        </Text>
        <Group className="dh-modal-footer" gap={12}>
          <Button
            variant="soft"
            color="gray"
            onClick={() => {
              setConflict(false);
              void autosave.resolve('theirs');
            }}
          >
            Reload theirs
          </Button>
          <Button
            color="red"
            onClick={() => {
              setConflict(false);
              void autosave.resolve('overwrite');
            }}
          >
            Overwrite
          </Button>
        </Group>
      </Modal>
    </>
  );
}
