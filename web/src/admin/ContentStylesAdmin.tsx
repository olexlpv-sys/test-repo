import { useState } from 'react';
import {
  Button,
  Checkbox,
  ColorInput,
  Group,
  Modal,
  NumberInput,
  Select,
  SimpleGrid,
  Stack,
  Table,
  Text,
  TextInput,
  Tooltip,
} from '@mantine/core';
import { modals } from '@mantine/modals';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError, unwrap, type Schemas } from '../api/client';
import { useContentSchema, useStylesheet } from '../document/api';

type Style = Schemas['ContentStyleResponse'];
type Kind = Schemas['ContentStyleKind'];
type Props = Record<string, unknown>;

const kinds: Kind[] = ['Paragraph', 'Character', 'Table'];
const sample = 'The quick brown fox jumps over the lazy dog.';

interface Form {
  id: number | null;
  styleId: string;
  kind: Kind;
  name: string;
  basedOnStyleId: string | null;
  rowVersion: string | null;
  isActive: boolean;
  properties: Props;
}

// Word units in the form: points; stored as half-points (font size) and twips (spacing, indents).
const pt = (twips: unknown) => (typeof twips === 'number' ? twips / 20 : '');
const halfPt = (value: unknown) => (typeof value === 'number' ? value / 2 : '');

function set(properties: Props, key: string, value: unknown): Props {
  // false stays: it overrides a flag inherited from the based-on style.
  const removed = value === null || value === undefined || value === '';
  return Object.fromEntries([
    ...Object.entries(properties).filter(([k]) => k !== key),
    ...(removed ? [] : [[key, value] as const]),
  ]);
}

/** A formatting flag: inherited from the based-on style, or explicitly on or off. */
function Flag({
  label,
  value,
  onChange,
}: {
  label: string;
  value: unknown;
  onChange: (value: boolean | null) => void;
}) {
  return (
    <Select
      label={label}
      w={130}
      allowDeselect={false}
      data={[
        { value: 'inherit', label: 'Inherit' },
        { value: 'on', label: 'On' },
        { value: 'off', label: 'Off' },
      ]}
      value={value === true ? 'on' : value === false ? 'off' : 'inherit'}
      onChange={(v) => onChange(v === 'on' ? true : v === 'off' ? false : null)}
      comboboxProps={{ withinPortal: true }}
    />
  );
}

const lineSpacings = [
  { value: '240', label: 'Single' },
  { value: '259', label: '1.08 (Word default)' },
  { value: '276', label: '1.15' },
  { value: '360', label: '1.5' },
  { value: '480', label: 'Double' },
];

function Preview({ style }: { style: Pick<Style, 'styleId' | 'kind'> }) {
  const cls = `ds-style-${style.styleId}`;
  if (style.kind === 'Character') {
    return (
      <span className="dh-style-preview">
        Normal text with <span className={cls}>{style.styleId} applied</span>.
      </span>
    );
  }

  if (style.kind === 'Table') {
    return (
      <table className={`${cls} dh-style-preview-table`}>
        <tbody>
          <tr>
            <td>A1</td>
            <td>B1</td>
          </tr>
          <tr>
            <td>A2</td>
            <td>B2</td>
          </tr>
        </tbody>
      </table>
    );
  }

  return <p className={`${cls} dh-style-preview`}>{sample}</p>;
}

/** Content styles (FR-T6): grid per kind with live previews; edit font, paragraph and table formatting; built-ins not deletable. */
export function ContentStylesAdmin() {
  const queryClient = useQueryClient();
  const [form, setForm] = useState<Form | null>(null);
  const schema = useContentSchema();
  const stylesheet = useStylesheet();
  const styles = useQuery({
    queryKey: ['content-styles', 'admin'],
    queryFn: () => unwrap(api.GET('/api/content-styles', { params: { query: { includeInactive: true } } })),
  });

  // The editor and every preview use the generated stylesheet: refetch it after any change.
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['content-styles'] });
    await queryClient.invalidateQueries({ queryKey: ['content-stylesheet'] });
  };

  const save = useMutation({
    mutationFn: (f: Form) =>
      f.id === null
        ? unwrap(
            api.POST('/api/content-styles', {
              body: {
                styleId: f.styleId.trim(),
                kind: f.kind,
                name: f.name.trim(),
                basedOnStyleId: f.basedOnStyleId,
                properties: f.properties,
              },
            }),
          )
        : unwrap(
            api.PUT('/api/content-styles/{id}', {
              params: { path: { id: f.id } },
              body: {
                name: f.name.trim(),
                basedOnStyleId: f.basedOnStyleId,
                properties: f.properties,
                isActive: f.isActive,
                rowVersion: f.rowVersion,
              },
            }),
          ),
    meta: { silent: true },
    onSuccess: async () => {
      setForm(null);
      await refresh();
    },
  });

  const remove = useMutation({
    mutationFn: (s: Style) =>
      unwrap(
        api.DELETE('/api/content-styles/{id}', { params: { path: { id: s.id }, query: { rowVersion: s.rowVersion } } }),
      ),
    onSuccess: refresh,
  });

  const open = (s: Style) => {
    save.reset();
    setForm({
      id: s.id,
      styleId: s.styleId,
      kind: s.kind,
      name: s.name,
      basedOnStyleId: s.basedOnStyleId,
      rowVersion: s.rowVersion,
      isActive: s.isActive,
      properties: (s.properties && typeof s.properties === 'object' ? s.properties : {}) as Props,
    });
  };

  const p = form?.properties ?? {};
  const update = (key: string, value: unknown) =>
    form && setForm({ ...form, properties: set(form.properties, key, value) });
  const errors =
    save.error instanceof ApiError
      ? Object.entries(save.error.errors ?? {}).map(([k, v]) => `${k}: ${v.join(' ')}`)
      : [];
  const allBorders = (p.borders as Record<string, { style?: string; size?: number; color?: string }> | undefined)?.top;

  return (
    <Stack gap={20}>
      {stylesheet.data && <style>{stylesheet.data}</style>}
      {kinds.map((kind) => (
        <section key={kind} className="dh-card" aria-label={`${kind} styles`}>
          <div className="dh-card-header">
            <span className="dh-card-title">{kind} styles</span>
            <Button
              variant="soft"
              onClick={() => {
                save.reset();
                setForm({
                  id: null,
                  styleId: '',
                  kind,
                  name: '',
                  basedOnStyleId: null,
                  rowVersion: null,
                  isActive: true,
                  properties: {},
                });
              }}
            >
              New {kind.toLowerCase()} style
            </Button>
          </div>
          <div className="dh-card-body" style={{ background: 'var(--dh-surface)' }}>
            <Table verticalSpacing={8}>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Style</Table.Th>
                  <Table.Th>Preview</Table.Th>
                  <Table.Th>Based on</Table.Th>
                  <Table.Th>Used</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {(styles.data ?? [])
                  .filter((s) => s.kind === kind)
                  .map((s) => (
                    <Table.Tr key={s.id} data-testid={`style-${s.styleId}`} data-inactive={!s.isActive || undefined}>
                      <Table.Td>
                        <Text fw={500} size="sm">
                          {s.name}
                        </Text>
                        <Text size="xs" className="dh-muted">
                          {s.styleId}
                          {s.isBuiltIn ? ' · built-in' : ''}
                          {s.isActive ? '' : ' · inactive'}
                        </Text>
                      </Table.Td>
                      <Table.Td style={{ maxWidth: 420 }}>
                        <Preview style={s} />
                      </Table.Td>
                      <Table.Td>{s.basedOnStyleId ?? '—'}</Table.Td>
                      <Table.Td>{s.usageCount}</Table.Td>
                      <Table.Td>
                        <Group gap={6} justify="flex-end" wrap="nowrap">
                          <Button size="compact-sm" variant="soft" onClick={() => open(s)}>
                            Edit
                          </Button>
                          {!s.isBuiltIn && (
                            <Tooltip label="In use: deactivate it instead" disabled={s.usageCount === 0}>
                              <span>
                                <Button
                                  size="compact-sm"
                                  variant="soft"
                                  color="red"
                                  disabled={s.usageCount > 0}
                                  onClick={() =>
                                    modals.openConfirmModal({
                                      title: `Delete style "${s.name}"?`,
                                      labels: { confirm: 'Delete', cancel: 'Cancel' },
                                      confirmProps: { color: 'red', variant: 'soft' },
                                      groupProps: { grow: true },
                                      onConfirm: () => remove.mutate(s),
                                    })
                                  }
                                >
                                  Delete
                                </Button>
                              </span>
                            </Tooltip>
                          )}
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  ))}
              </Table.Tbody>
            </Table>
          </div>
        </section>
      ))}

      <Modal
        opened={form !== null}
        onClose={() => setForm(null)}
        title={form?.id === null ? `New ${form.kind.toLowerCase()} style` : `Edit ${form?.name ?? ''}`}
        size={640}
      >
        {form && (
          <form
            onSubmit={(e) => {
              e.preventDefault();
              save.mutate(form);
            }}
          >
            <Stack gap={14}>
              <SimpleGrid cols={2} spacing={12}>
                <TextInput
                  label="Style id"
                  value={form.styleId}
                  disabled={form.id !== null}
                  onChange={(e) => setForm({ ...form, styleId: e.currentTarget.value })}
                />
                <TextInput
                  label="Name"
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.currentTarget.value })}
                />
                <Select
                  label="Based on"
                  clearable
                  data={(styles.data ?? [])
                    .filter((s) => s.kind === form.kind && s.styleId !== form.styleId)
                    .map((s) => ({ value: s.styleId, label: s.name }))}
                  value={form.basedOnStyleId}
                  onChange={(v) => setForm({ ...form, basedOnStyleId: v })}
                  comboboxProps={{ withinPortal: true }}
                />
                {form.id !== null && (
                  <Checkbox
                    mt={30}
                    label="Active"
                    checked={form.isActive}
                    onChange={(e) => setForm({ ...form, isActive: e.currentTarget.checked })}
                  />
                )}
              </SimpleGrid>

              <Text fw={500} size="sm">
                Font
              </Text>
              <SimpleGrid cols={3} spacing={12}>
                <Select
                  label="Font family"
                  clearable
                  data={schema.data?.fontFamilies ?? []}
                  value={(p.fontFamily as string | undefined) ?? null}
                  onChange={(v) => update('fontFamily', v)}
                  comboboxProps={{ withinPortal: true }}
                />
                <NumberInput
                  label="Font size (pt)"
                  min={1}
                  max={200}
                  step={0.5}
                  decimalScale={1}
                  value={halfPt(p.fontSize)}
                  onChange={(v) => update('fontSize', v === '' ? null : Math.round(Number(v) * 2))}
                />
                <ColorInput
                  label="Color"
                  format="hex"
                  value={(p.color as string | undefined) ?? ''}
                  onChange={(v) => update('color', /^#[0-9a-fA-F]{6}$/.test(v) ? v.toUpperCase() : null)}
                />
              </SimpleGrid>
              <Group gap={20}>
                <Flag label="Bold" value={p.bold} onChange={(v) => update('bold', v)} />
                <Flag label="Italic" value={p.italic} onChange={(v) => update('italic', v)} />
                <Flag label="Strikethrough" value={p.strike} onChange={(v) => update('strike', v)} />
                <Flag label="Small caps" value={p.smallCaps} onChange={(v) => update('smallCaps', v)} />
              </Group>

              {form.kind !== 'Character' && (
                <>
                  <Text fw={500} size="sm">
                    Paragraph
                  </Text>
                  <SimpleGrid cols={3} spacing={12}>
                    <Select
                      label="Alignment"
                      clearable
                      data={['left', 'center', 'right', 'justify']}
                      value={(p.align as string | undefined) ?? null}
                      onChange={(v) => update('align', v)}
                      comboboxProps={{ withinPortal: true }}
                    />
                    <NumberInput
                      label="Space before (pt)"
                      min={0}
                      max={1584}
                      value={pt(p.spacingBefore)}
                      onChange={(v) => update('spacingBefore', v === '' ? null : Math.round(Number(v) * 20))}
                    />
                    <NumberInput
                      label="Space after (pt)"
                      min={0}
                      max={1584}
                      value={pt(p.spacingAfter)}
                      onChange={(v) => update('spacingAfter', v === '' ? null : Math.round(Number(v) * 20))}
                    />
                    <Select
                      label="Line spacing"
                      clearable
                      data={
                        // Any stored value is shown, not only the presets.
                        typeof p.lineSpacing === 'number' &&
                        !lineSpacings.some((l) => l.value === String(p.lineSpacing))
                          ? [...lineSpacings, { value: String(p.lineSpacing), label: (p.lineSpacing / 240).toFixed(2) }]
                          : lineSpacings
                      }
                      value={
                        typeof p.lineSpacing === 'number' && (p.lineRule ?? 'auto') === 'auto'
                          ? String(p.lineSpacing)
                          : null
                      }
                      onChange={(v) =>
                        form &&
                        setForm({
                          ...form,
                          properties: set(
                            set(form.properties, 'lineSpacing', v ? Number(v) : null),
                            'lineRule',
                            v ? 'auto' : null,
                          ),
                        })
                      }
                      comboboxProps={{ withinPortal: true }}
                    />
                    <NumberInput
                      label="Indent left (pt)"
                      min={0}
                      max={1584}
                      value={pt(p.indentLeft)}
                      onChange={(v) => update('indentLeft', v === '' ? null : Math.round(Number(v) * 20))}
                    />
                    <NumberInput
                      label="Indent right (pt)"
                      min={0}
                      max={1584}
                      value={pt(p.indentRight)}
                      onChange={(v) => update('indentRight', v === '' ? null : Math.round(Number(v) * 20))}
                    />
                    <NumberInput
                      label="First line (pt)"
                      min={0}
                      max={1584}
                      value={pt(p.indentFirstLine)}
                      onChange={(v) => update('indentFirstLine', v === '' ? null : Math.round(Number(v) * 20))}
                    />
                    <ColorInput
                      label="Shading"
                      format="hex"
                      value={(p.shading as string | undefined) ?? ''}
                      onChange={(v) => update('shading', /^#[0-9a-fA-F]{6}$/.test(v) ? v.toUpperCase() : null)}
                    />
                  </SimpleGrid>
                  <Flag label="Keep with next" value={p.keepWithNext} onChange={(v) => update('keepWithNext', v)} />
                </>
              )}

              {form.kind === 'Table' && (
                <>
                  <Text fw={500} size="sm">
                    Table borders (all sides and inside lines)
                  </Text>
                  <SimpleGrid cols={3} spacing={12}>
                    <Select
                      label="Border style"
                      clearable
                      data={['single', 'double', 'dotted', 'dashed', 'thick', 'none']}
                      value={allBorders?.style ?? null}
                      onChange={(v) =>
                        update(
                          'borders',
                          v
                            ? Object.fromEntries(
                                ['top', 'left', 'bottom', 'right', 'insideH', 'insideV'].map((side) => [
                                  side,
                                  { style: v, size: allBorders?.size ?? 4, color: allBorders?.color ?? '#000000' },
                                ]),
                              )
                            : null,
                        )
                      }
                      comboboxProps={{ withinPortal: true }}
                    />
                    <NumberInput
                      label="Border width (1/8 pt)"
                      min={2}
                      max={96}
                      disabled={!allBorders}
                      value={allBorders?.size ?? 4}
                      onChange={(v) =>
                        allBorders &&
                        update(
                          'borders',
                          Object.fromEntries(
                            ['top', 'left', 'bottom', 'right', 'insideH', 'insideV'].map((side) => [
                              side,
                              { ...allBorders, size: Number(v) || 4 },
                            ]),
                          ),
                        )
                      }
                    />
                    <ColorInput
                      label="Border color"
                      format="hex"
                      disabled={!allBorders}
                      value={allBorders?.color ?? '#000000'}
                      onChange={(v) =>
                        allBorders &&
                        /^#[0-9a-fA-F]{6}$/.test(v) &&
                        update(
                          'borders',
                          Object.fromEntries(
                            ['top', 'left', 'bottom', 'right', 'insideH', 'insideV'].map((side) => [
                              side,
                              { ...allBorders, color: v.toUpperCase() },
                            ]),
                          ),
                        )
                      }
                    />
                  </SimpleGrid>
                </>
              )}

              {errors.length > 0 && (
                <Text c="red" size="sm" data-testid="style-errors">
                  {errors.join(' · ')}
                </Text>
              )}
              {save.error instanceof ApiError && !save.error.errors && (
                <Text c="red" size="sm">
                  {save.error.detail ?? save.error.title}
                </Text>
              )}
            </Stack>
            <Group className="dh-modal-footer" gap={12}>
              <Button variant="soft" color="gray" onClick={() => setForm(null)}>
                Cancel
              </Button>
              <Button type="submit" loading={save.isPending} disabled={!form.styleId.trim() || !form.name.trim()}>
                Save
              </Button>
            </Group>
          </form>
        )}
      </Modal>
    </Stack>
  );
}
