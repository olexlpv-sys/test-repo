import { useState } from 'react';
import {
  Button,
  Group,
  Modal,
  NumberInput,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Textarea,
  Tooltip,
} from '@mantine/core';
import { modals } from '@mantine/modals';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError, unwrap, type Schemas } from '../api/client';

type NodeType = Schemas['NodeTypeResponse'];
type Form = {
  id: number | null;
  code: string;
  name: string;
  description: string;
  sortOrder: number;
  rowVersion: string | null;
  isActive: boolean;
};

const blank: Form = { id: null, code: '', name: '', description: '', sortOrder: 0, rowVersion: null, isActive: true };

/** Field errors of a ProblemDetails answer (validation or duplicate code). */
function fieldError(error: unknown, field: string): string | undefined {
  if (!(error instanceof ApiError)) {
    return undefined;
  }

  const key = Object.keys(error.errors ?? {}).find((k) => k.toLowerCase() === field.toLowerCase());
  if (key) {
    return error.errors?.[key]?.join(' ');
  }

  return field === 'code' && error.type === 'duplicate-name' ? 'This code is already used.' : undefined;
}

/** Node types (FR-N1, FR-N2): grid, add/edit, activate/deactivate, delete only when unused. */
export function NodeTypesAdmin() {
  const queryClient = useQueryClient();
  const [form, setForm] = useState<Form | null>(null);
  const types = useQuery({
    queryKey: ['node-types', 'all'],
    queryFn: () => unwrap(api.GET('/api/node-types', { params: { query: { includeInactive: true } } })),
  });

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['node-types'] });

  const save = useMutation({
    mutationFn: (f: Form) => {
      const body = {
        code: f.code.trim(),
        name: f.name.trim(),
        description: f.description.trim() || null,
        sortOrder: f.sortOrder,
      };
      return f.id === null
        ? unwrap(api.POST('/api/node-types', { body }))
        : unwrap(
            api.PUT('/api/node-types/{id}', {
              params: { path: { id: f.id } },
              body: { ...body, isActive: f.isActive, rowVersion: f.rowVersion },
            }),
          );
    },
    meta: { silent: true }, // shown on the fields
    onSuccess: async () => {
      setForm(null);
      await refresh();
    },
  });

  const toggle = useMutation({
    mutationFn: (t: NodeType) =>
      unwrap(
        api.PUT('/api/node-types/{id}', {
          params: { path: { id: t.id } },
          body: {
            code: t.code,
            name: t.name,
            description: t.description,
            sortOrder: t.sortOrder,
            isActive: !t.isActive,
            rowVersion: t.rowVersion,
          },
        }),
      ),
    onSuccess: refresh,
  });

  const remove = useMutation({
    mutationFn: (t: NodeType) => unwrap(api.DELETE('/api/node-types/{id}', { params: { path: { id: t.id } } })),
    onSuccess: refresh,
  });

  const edit = (t: NodeType) =>
    setForm({
      id: t.id,
      code: t.code,
      name: t.name,
      description: t.description ?? '',
      sortOrder: t.sortOrder,
      rowVersion: t.rowVersion,
      isActive: t.isActive,
    });

  return (
    <section className="dh-card" aria-label="Node types">
      <div className="dh-card-header">
        <span className="dh-card-title">Node types</span>
        <Button
          onClick={() => {
            save.reset();
            setForm({
              ...blank,
              sortOrder: ((types.data ?? []).reduce((m, t) => Math.max(m, t.sortOrder), 0) || 0) + 10,
            });
          }}
        >
          Add node type
        </Button>
      </div>
      <div className="dh-card-body" style={{ background: 'var(--dh-surface)' }}>
        <Table verticalSpacing={8} data-testid="node-types">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Code</Table.Th>
              <Table.Th>Name</Table.Th>
              <Table.Th>Description</Table.Th>
              <Table.Th>Sort order</Table.Th>
              <Table.Th>Active</Table.Th>
              <Table.Th>Used by</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(types.data ?? []).map((t) => (
              <Table.Tr key={t.id} data-testid={`node-type-${t.code}`} data-inactive={!t.isActive || undefined}>
                <Table.Td>{t.code}</Table.Td>
                <Table.Td>{t.name}</Table.Td>
                <Table.Td>
                  <Text size="sm" className="dh-muted" lineClamp={2}>
                    {t.description}
                  </Text>
                </Table.Td>
                <Table.Td>{t.sortOrder}</Table.Td>
                <Table.Td>
                  <Switch
                    aria-label={`${t.isActive ? 'Deactivate' : 'Activate'} ${t.name}`}
                    checked={t.isActive}
                    onChange={() => toggle.mutate(t)}
                  />
                </Table.Td>
                <Table.Td>
                  {t.usageCount} node{t.usageCount === 1 ? '' : 's'}
                </Table.Td>
                <Table.Td>
                  <Group gap={6} justify="flex-end" wrap="nowrap">
                    <Button
                      size="compact-sm"
                      variant="soft"
                      onClick={() => {
                        save.reset();
                        edit(t);
                      }}
                    >
                      Edit
                    </Button>
                    <Tooltip label="Used by nodes: deactivate it instead" disabled={t.usageCount === 0}>
                      <span>
                        <Button
                          size="compact-sm"
                          variant="soft"
                          color="red"
                          disabled={t.usageCount > 0}
                          onClick={() =>
                            modals.openConfirmModal({
                              title: `Delete node type "${t.name}"?`,
                              labels: { confirm: 'Delete', cancel: 'Cancel' },
                              confirmProps: { color: 'red', variant: 'soft' },
                              groupProps: { grow: true },
                              onConfirm: () => remove.mutate(t),
                            })
                          }
                        >
                          Delete
                        </Button>
                      </span>
                    </Tooltip>
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </div>

      <Modal
        opened={form !== null}
        onClose={() => setForm(null)}
        title={form?.id === null ? 'New node type' : 'Edit node type'}
      >
        {form && (
          <form
            onSubmit={(e) => {
              e.preventDefault();
              save.mutate(form);
            }}
          >
            <Stack>
              <TextInput
                label="Code"
                data-autofocus
                value={form.code}
                onChange={(e) => setForm({ ...form, code: e.currentTarget.value })}
                error={fieldError(save.error, 'code')}
              />
              <TextInput
                label="Name"
                value={form.name}
                onChange={(e) => setForm({ ...form, name: e.currentTarget.value })}
                error={fieldError(save.error, 'name')}
              />
              <Textarea
                label="Description"
                autosize
                minRows={2}
                value={form.description}
                onChange={(e) => setForm({ ...form, description: e.currentTarget.value })}
                error={fieldError(save.error, 'description')}
              />
              <NumberInput
                label="Sort order"
                allowDecimal={false}
                value={form.sortOrder}
                onChange={(v) => setForm({ ...form, sortOrder: Number(v) || 0 })}
                error={fieldError(save.error, 'sortOrder')}
              />
              {save.error instanceof ApiError && !save.error.errors && save.error.type !== 'duplicate-name' && (
                <Text c="red" size="sm">
                  {save.error.detail ?? save.error.title}
                </Text>
              )}
            </Stack>
            <Group className="dh-modal-footer" gap={12}>
              <Button variant="soft" color="gray" onClick={() => setForm(null)}>
                Cancel
              </Button>
              <Button type="submit" loading={save.isPending} disabled={!form.code.trim() || !form.name.trim()}>
                Save
              </Button>
            </Group>
          </form>
        )}
      </Modal>
    </section>
  );
}
