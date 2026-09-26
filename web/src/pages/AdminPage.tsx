import { Stack, Text } from '@mantine/core';
import { useSearchParams } from 'react-router-dom';
import { AuditAdmin, FindingsAdmin } from '../admin/AuditAdmin';
import { ContentStylesAdmin } from '../admin/ContentStylesAdmin';
import { FoldersAdmin } from '../admin/FoldersAdmin';
import { NodeTypesAdmin } from '../admin/NodeTypesAdmin';
import { UsersAdmin } from '../admin/UsersAdmin';
import { useSession } from '../app/sessionContext';

const tabs = [
  { value: 'node-types', label: 'Node types' },
  { value: 'styles', label: 'Content styles' },
  { value: 'users', label: 'Users' },
  { value: 'folders', label: 'Folders' },
  { value: 'audit', label: 'Audit log' },
] as const;

/** Admin tab (FR-UI3, T17): supporting entities, admins only; the sub-tab is in the URL (?tab=). */
export function AdminPage() {
  const { me } = useSession();
  const [params, setParams] = useSearchParams();
  if (!me?.isAdmin) {
    return (
      <Text c="red" role="alert">
        Access denied — the Admin tab is for administrators.
      </Text>
    );
  }

  const tab = tabs.find((t) => t.value === params.get('tab'))?.value ?? 'node-types';
  return (
    <Stack gap={20}>
      <nav className="dh-tabs dh-subtabs" role="tablist" aria-label="Admin sections">
        {tabs.map((t) => (
          <button
            key={t.value}
            type="button"
            role="tab"
            aria-selected={tab === t.value}
            className="dh-tab"
            data-active={tab === t.value}
            onClick={() => setParams({ tab: t.value })}
          >
            {t.label}
          </button>
        ))}
      </nav>
      {tab === 'node-types' && <NodeTypesAdmin />}
      {tab === 'styles' && <ContentStylesAdmin />}
      {tab === 'users' && <UsersAdmin />}
      {tab === 'folders' && <FoldersAdmin />}
      {tab === 'audit' && (
        <>
          <FindingsAdmin />
          <AuditAdmin />
        </>
      )}
    </Stack>
  );
}
