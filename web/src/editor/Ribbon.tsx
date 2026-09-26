import { useState, type ReactNode } from 'react';
import {
  ActionIcon,
  Button,
  Divider,
  Group,
  Menu,
  Popover,
  Select,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
  Tooltip,
  UnstyledButton,
} from '@mantine/core';
import { useEditorState } from '@tiptap/react';
import type { Editor } from '@tiptap/core';
import type { ContentSchemaInfo } from './contentSchema';

export interface ParagraphStyle {
  styleId: string;
  name: string;
}

const FontSizes = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72];
const LineSpacings = [
  { label: '1.0', value: 240 },
  { label: '1.15', value: 276 },
  { label: '1.5', value: 360 },
  { label: '2.0', value: 480 },
];
const ParagraphSpacings = [0, 6, 12, 18, 24]; // pt
const Indent = 720; // twips = 0.5"

function Tool({
  label,
  active,
  disabled,
  onClick,
  children,
}: {
  label: string;
  active?: boolean;
  disabled?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <Tooltip label={label} openDelay={400}>
      <ActionIcon
        variant={active ? 'soft' : 'subtle'}
        color={active ? 'brand' : 'gray'}
        size={32}
        aria-label={label}
        aria-pressed={active ?? false}
        disabled={disabled}
        onMouseDown={(e) => e.preventDefault()} // keep the section's selection
        onClick={onClick}
      >
        {children}
      </ActionIcon>
    </Tooltip>
  );
}

/** Paragraph attributes of the blocks in the selection (paragraphs and headings). */
function setParagraph(editor: Editor, attrs: Record<string, unknown>) {
  editor.chain().focus().updateAttributes('paragraph', attrs).updateAttributes('heading', attrs).run();
}

function currentBlockAttr(editor: Editor, name: string): unknown {
  const attrs = editor.isActive('heading') ? editor.getAttributes('heading') : editor.getAttributes('paragraph');
  return attrs[name];
}

function ColorTool({
  label,
  value,
  onChange,
  children,
}: {
  label: string;
  value: string | null;
  onChange: (color: string | null) => void;
  children: ReactNode;
}) {
  return (
    <Menu position="bottom-start" withinPortal>
      <Menu.Target>
        <ActionIcon variant="subtle" color="gray" size={32} aria-label={label} onMouseDown={(e) => e.preventDefault()}>
          <Stack gap={0} align="center">
            {children}
            <span
              style={{
                width: 16,
                height: 3,
                background: value ?? 'transparent',
                border: value ? 'none' : '1px solid #d0d5dd',
              }}
            />
          </Stack>
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <SimpleGrid cols={6} spacing={4} p={4}>
          {[
            '#000000',
            '#404040',
            '#7F7F7F',
            '#C00000',
            '#FF0000',
            '#FFC000',
            '#FFFF00',
            '#92D050',
            '#00B050',
            '#00B0F0',
            '#0070C0',
            '#002060',
            '#7030A0',
            '#F2DCDB',
            '#DDEBF7',
            '#E2EFDA',
            '#FFF2CC',
            '#FFFFFF',
          ].map((color) => (
            <UnstyledButton
              key={color}
              aria-label={`${label} ${color}`}
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => onChange(color)}
              style={{ width: 20, height: 20, background: color, border: '1px solid #d0d5dd', borderRadius: 4 }}
            />
          ))}
        </SimpleGrid>
        <Menu.Item onClick={() => onChange(null)}>Automatic / none</Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

function TablePicker({ onPick }: { onPick: (rows: number, cols: number) => void }) {
  const [hover, setHover] = useState({ rows: 0, cols: 0 });
  const [opened, setOpened] = useState(false);
  return (
    <Popover opened={opened} onChange={setOpened} position="bottom-start" withinPortal>
      <Popover.Target>
        <Button
          variant="subtle"
          color="gray"
          size="compact-sm"
          onMouseDown={(e) => e.preventDefault()}
          onClick={() => setOpened((o) => !o)}
        >
          ⊞ Table
        </Button>
      </Popover.Target>
      <Popover.Dropdown>
        <Text size="xs" mb={6}>
          {hover.rows > 0 ? `${hover.rows} × ${hover.cols} table` : 'Insert table'}
        </Text>
        <div
          style={{ display: 'grid', gridTemplateColumns: 'repeat(8, 16px)', gap: 3 }}
          onMouseLeave={() => setHover({ rows: 0, cols: 0 })}
        >
          {Array.from({ length: 64 }, (_, i) => {
            const row = Math.floor(i / 8) + 1;
            const col = (i % 8) + 1;
            const on = row <= hover.rows && col <= hover.cols;
            return (
              <button
                key={i}
                type="button"
                aria-label={`${row} by ${col}`}
                onMouseEnter={() => setHover({ rows: row, cols: col })}
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => {
                  setOpened(false);
                  onPick(row, col);
                }}
                style={{
                  width: 16,
                  height: 16,
                  padding: 0,
                  border: '1px solid #d0d5dd',
                  borderRadius: 2,
                  background: on ? '#eaf1fb' : '#fff',
                }}
              />
            );
          })}
        </div>
      </Popover.Dropdown>
    </Popover>
  );
}

function LinkTool({ editor }: { editor: Editor }) {
  const [opened, setOpened] = useState(false);
  const [href, setHref] = useState('');
  return (
    <Popover
      opened={opened}
      onChange={setOpened}
      position="bottom-start"
      withinPortal
      trapFocus
      onOpen={() => setHref(String(editor.getAttributes('link').href ?? ''))}
    >
      <Popover.Target>
        <Button
          variant="subtle"
          color="gray"
          size="compact-sm"
          onMouseDown={(e) => e.preventDefault()}
          onClick={() => setOpened((o) => !o)}
        >
          🔗 Link
        </Button>
      </Popover.Target>
      <Popover.Dropdown>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            const value = href.trim();
            const chain = editor.chain().focus().extendMarkRange('link');
            if (value && /^(https?:\/\/|mailto:)/i.test(value)) {
              chain.setLink({ href: value }).run();
            } else {
              chain.unsetLink().run();
            }

            setOpened(false);
          }}
        >
          <Group gap={8} align="flex-end">
            <TextInput
              label="Address (http, https or mailto)"
              value={href}
              onChange={(e) => setHref(e.currentTarget.value)}
              w={260}
              data-autofocus
            />
            <Button type="submit">Apply</Button>
          </Group>
        </form>
      </Popover.Dropdown>
    </Popover>
  );
}

const editorIds = new WeakMap<Editor, number>();
let nextEditorId = 1;
const editorId = (editor: Editor | null) => {
  if (!editor) {
    return 0;
  }

  let id = editorIds.get(editor);
  if (id === undefined) {
    id = nextEditorId++;
    editorIds.set(editor, id);
  }

  return id;
};

/** Word-like toolbar (T15 §3): Styles · Font · Paragraph · Insert · Table (contextual); acts on the focused section. */
export function Ribbon(props: { editor: Editor | null; schema: ContentSchemaInfo; styles: ParagraphStyle[] }) {
  // A new section editor gets a fresh toolbar state at once (not only after its next transaction).
  return <RibbonFor key={editorId(props.editor)} {...props} />;
}

function RibbonFor({
  editor,
  schema,
  styles,
}: {
  editor: Editor | null;
  schema: ContentSchemaInfo;
  styles: ParagraphStyle[];
}) {
  const state = useEditorState({
    editor,
    selector: ({ editor: e }) =>
      e && !e.isDestroyed
        ? {
            editable: e.isEditable,
            bold: e.isActive('bold'),
            italic: e.isActive('italic'),
            underline: e.isActive('underline'),
            strike: e.isActive('strike'),
            subscript: e.isActive('subscript'),
            superscript: e.isActive('superscript'),
            bullet: e.isActive('bulletList'),
            ordered: e.isActive('orderedList'),
            inTable: e.isActive('table'),
            inListItem: e.isActive('listItem'),
            heading: e.isActive('heading') ? Number(e.getAttributes('heading').level) : 0,
            styleId: (currentBlockAttr(e, 'styleId') as string | null) ?? null,
            align: (currentBlockAttr(e, 'align') as string | null) ?? 'left',
            fontFamily: (e.getAttributes('textStyle').fontFamily as string | undefined) ?? null,
            fontSize: (e.getAttributes('textStyle').fontSize as number | undefined) ?? null,
            color: (e.getAttributes('textStyle').color as string | undefined) ?? null,
            highlight: (e.getAttributes('highlight').color as string | undefined) ?? null,
          }
        : null,
  });

  const disabled = !editor || !state?.editable;
  const ed = editor as Editor; // used only when enabled
  const styleValue = state ? (state.heading ? `Heading${state.heading}` : (state.styleId ?? 'Normal')) : null;

  return (
    <div className="dh-ribbon" role="toolbar" aria-label="Formatting">
      <Group gap={6} wrap="wrap">
        <Select
          aria-label="Style"
          w={150}
          size="xs"
          disabled={disabled}
          data={[
            { value: 'Normal', label: 'Normal' },
            ...styles.filter((s) => s.styleId !== 'Normal').map((s) => ({ value: s.styleId, label: s.name })),
          ]}
          value={styleValue}
          allowDeselect={false}
          renderOption={({ option }) => (
            <span className={`ds-style-${option.value} dh-style-option`}>{option.label}</span>
          )}
          onChange={(value) => {
            if (!value) {
              return;
            }

            const heading = value.match(/^Heading([1-6])$/);
            if (heading) {
              ed.chain()
                .focus()
                .setNode('heading', { level: Number(heading[1]), styleId: null })
                .run();
            } else {
              ed.chain()
                .focus()
                .setNode('paragraph', { styleId: value === 'Normal' ? null : value })
                .run();
            }
          }}
          comboboxProps={{ withinPortal: true }}
        />
        <Divider orientation="vertical" />
        <Select
          aria-label="Font"
          w={140}
          size="xs"
          disabled={disabled}
          placeholder="Font"
          data={schema.fontFamilies}
          value={state?.fontFamily ?? null}
          renderOption={({ option }) => <span style={{ fontFamily: `"${option.value}"` }}>{option.label}</span>}
          onChange={(value) =>
            ed.chain().focus().setMark('textStyle', { fontFamily: value }).removeEmptyTextStyle().run()
          }
          comboboxProps={{ withinPortal: true }}
        />
        <Select
          aria-label="Font size"
          w={72}
          size="xs"
          disabled={disabled}
          placeholder="Size"
          data={FontSizes.map((s) => ({ value: String(s * 2), label: String(s) }))}
          value={state?.fontSize ? String(state.fontSize) : null}
          onChange={(value) =>
            ed
              .chain()
              .focus()
              .setMark('textStyle', { fontSize: value ? Number(value) : null })
              .removeEmptyTextStyle()
              .run()
          }
          comboboxProps={{ withinPortal: true }}
        />
        <Tool
          label="Bold"
          active={state?.bold}
          disabled={disabled}
          onClick={() => ed.chain().focus().toggleBold().run()}
        >
          <b>B</b>
        </Tool>
        <Tool
          label="Italic"
          active={state?.italic}
          disabled={disabled}
          onClick={() => ed.chain().focus().toggleItalic().run()}
        >
          <i>I</i>
        </Tool>
        <Tool
          label="Underline"
          active={state?.underline}
          disabled={disabled}
          onClick={() => ed.chain().focus().toggleUnderline().run()}
        >
          <u>U</u>
        </Tool>
        <Tool
          label="Strikethrough"
          active={state?.strike}
          disabled={disabled}
          onClick={() => ed.chain().focus().toggleStrike().run()}
        >
          <s>S</s>
        </Tool>
        <Tool
          label="Subscript"
          active={state?.subscript}
          disabled={disabled}
          onClick={() => ed.chain().focus().toggleSubscript().run()}
        >
          x₂
        </Tool>
        <Tool
          label="Superscript"
          active={state?.superscript}
          disabled={disabled}
          onClick={() => ed.chain().focus().toggleSuperscript().run()}
        >
          x²
        </Tool>
        {!disabled && (
          <>
            <ColorTool
              label="Font color"
              value={state?.color ?? null}
              onChange={(color) => ed.chain().focus().setMark('textStyle', { color }).removeEmptyTextStyle().run()}
            >
              <span style={{ fontWeight: 600 }}>A</span>
            </ColorTool>
            <ColorTool
              label="Highlight"
              value={state?.highlight ?? null}
              onChange={(color) =>
                color ? ed.chain().focus().setHighlight({ color }).run() : ed.chain().focus().unsetHighlight().run()
              }
            >
              <span style={{ background: '#fff2c2', padding: '0 2px' }}>ab</span>
            </ColorTool>
          </>
        )}
        <Tool label="Clear formatting" disabled={disabled} onClick={() => ed.chain().focus().unsetAllMarks().run()}>
          ⌫
        </Tool>
        <Divider orientation="vertical" />
        {(['left', 'center', 'right', 'justify'] as const).map((align) => (
          <Tool
            key={align}
            label={`Align ${align}`}
            active={state?.align === align}
            disabled={disabled}
            onClick={() => setParagraph(ed, { align: align === 'left' ? null : align })}
          >
            {{ left: '⯇', center: '≡', right: '⯈', justify: '☰' }[align]}
          </Tool>
        ))}
        <Tool
          label="Bullets"
          active={state?.bullet}
          disabled={disabled}
          onClick={() => ed.chain().focus().toggleBulletList().run()}
        >
          •
        </Tool>
        <Tool
          label="Numbering"
          active={state?.ordered}
          disabled={disabled}
          onClick={() => ed.chain().focus().toggleOrderedList().run()}
        >
          1.
        </Tool>
        <Tool
          label="Decrease indent"
          disabled={disabled}
          onClick={() => {
            if (!ed.chain().focus().liftListItem('listItem').run()) {
              const current = Number(currentBlockAttr(ed, 'indentLeft') ?? 0);
              setParagraph(ed, { indentLeft: current - Indent > 0 ? current - Indent : null });
            }
          }}
        >
          ⇤
        </Tool>
        <Tool
          label="Increase indent"
          disabled={disabled}
          onClick={() => {
            if (!ed.chain().focus().sinkListItem('listItem').run()) {
              setParagraph(ed, {
                indentLeft: Math.min(31680, Number(currentBlockAttr(ed, 'indentLeft') ?? 0) + Indent),
              });
            }
          }}
        >
          ⇥
        </Tool>
        <Menu withinPortal position="bottom-start">
          <Menu.Target>
            <Button
              variant="subtle"
              color="gray"
              size="compact-sm"
              disabled={disabled}
              onMouseDown={(e) => e.preventDefault()}
            >
              ↕ Spacing
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Label>Line spacing</Menu.Label>
            {LineSpacings.map((l) => (
              <Menu.Item
                key={l.value}
                onClick={() =>
                  setParagraph(ed, {
                    lineSpacing: l.value === 240 ? null : l.value,
                    lineRule: l.value === 240 ? null : 'auto',
                  })
                }
              >
                {l.label}
              </Menu.Item>
            ))}
            <Menu.Label>Space before</Menu.Label>
            {ParagraphSpacings.map((p) => (
              <Menu.Item key={`b${p}`} onClick={() => setParagraph(ed, { spacingBefore: p * 20 })}>
                {p} pt before
              </Menu.Item>
            ))}
            <Menu.Label>Space after</Menu.Label>
            {ParagraphSpacings.map((p) => (
              <Menu.Item key={`a${p}`} onClick={() => setParagraph(ed, { spacingAfter: p * 20 })}>
                {p} pt after
              </Menu.Item>
            ))}
          </Menu.Dropdown>
        </Menu>
        <Divider orientation="vertical" />
        {/* Tables and page breaks only at the top level: Content Schema v1 allows neither in table cells or list items. */}
        {!disabled && !state?.inTable && !state?.inListItem && (
          <>
            <TablePicker
              onPick={(rows, cols) => ed.chain().focus().insertTable({ rows, cols, withHeaderRow: false }).run()}
            />
            <Button
              variant="subtle"
              color="gray"
              size="compact-sm"
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => ed.chain().focus().setPageBreak().run()}
            >
              ⤓ Page break
            </Button>
            <LinkTool editor={ed} />
          </>
        )}
        {!disabled && state?.inTable && (
          <>
            <Divider orientation="vertical" />
            <Menu withinPortal position="bottom-start">
              <Menu.Target>
                <Button variant="soft" size="compact-sm" onMouseDown={(e) => e.preventDefault()}>
                  Table ▾
                </Button>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item onClick={() => ed.chain().focus().addRowBefore().run()}>Insert row above</Menu.Item>
                <Menu.Item onClick={() => ed.chain().focus().addRowAfter().run()}>Insert row below</Menu.Item>
                <Menu.Item onClick={() => ed.chain().focus().addColumnBefore().run()}>Insert column left</Menu.Item>
                <Menu.Item onClick={() => ed.chain().focus().addColumnAfter().run()}>Insert column right</Menu.Item>
                <Menu.Divider />
                <Menu.Item onClick={() => ed.chain().focus().mergeCells().run()}>Merge cells</Menu.Item>
                <Menu.Item onClick={() => ed.chain().focus().splitCell().run()}>Split cell</Menu.Item>
                <Menu.Divider />
                <Menu.Item
                  onClick={() =>
                    ed
                      .chain()
                      .focus()
                      .setCellAttribute('borders', {
                        top: { style: 'single', size: 4, color: '#000000' },
                        left: { style: 'single', size: 4, color: '#000000' },
                        bottom: { style: 'single', size: 4, color: '#000000' },
                        right: { style: 'single', size: 4, color: '#000000' },
                      })
                      .run()
                  }
                >
                  All borders
                </Menu.Item>
                <Menu.Item onClick={() => ed.chain().focus().setCellAttribute('borders', null).run()}>
                  No borders
                </Menu.Item>
                <Menu.Divider />
                <Menu.Item color="red" onClick={() => ed.chain().focus().deleteRow().run()}>
                  Delete row
                </Menu.Item>
                <Menu.Item color="red" onClick={() => ed.chain().focus().deleteColumn().run()}>
                  Delete column
                </Menu.Item>
                <Menu.Item color="red" onClick={() => ed.chain().focus().deleteTable().run()}>
                  Delete table
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
            <ColorTool
              label="Cell shading"
              value={null}
              onChange={(color) => ed.chain().focus().setCellAttribute('shading', color).run()}
            >
              <span>▦</span>
            </ColorTool>
          </>
        )}
      </Group>
    </div>
  );
}
