import type { ReactNode } from 'react';
import { Group } from '@mantine/core';
import { useLocation, useNavigate } from 'react-router-dom';
import { useSession } from './sessionContext';
import { ActingAs } from '../components/ActingAs';

/** Header: product name, Documents | Admin tabs (Admin for admins only), test-mode chip and user switcher (FR-UI4). */
export function Shell({ children }: { children: ReactNode }) {
  const { testMode, me } = useSession();
  const navigate = useNavigate();
  const location = useLocation();
  const tab = location.pathname.startsWith('/admin') ? 'admin' : 'documents';
  const tabs = [
    { value: 'documents', label: 'Documents', path: '/' },
    ...(me?.isAdmin ? [{ value: 'admin', label: 'Admin', path: '/admin' }] : []),
  ];
  return (
    <>
      <header className="dh-topbar">
        <Group h="100%" gap={0} wrap="nowrap">
          <span className="dh-brand">DocHub</span>
          <nav className="dh-tabs" role="tablist">
            {tabs.map((t) => (
              <button
                key={t.value}
                type="button"
                role="tab"
                aria-selected={tab === t.value}
                className="dh-tab"
                data-active={tab === t.value}
                onClick={() => navigate(t.path)}
              >
                {t.label}
              </button>
            ))}
          </nav>
        </Group>
        {testMode && (
          <Group gap="sm" wrap="nowrap">
            <span className="dh-chip" data-tone="amber">
              Test mode
            </span>
            <ActingAs />
          </Group>
        )}
      </header>
      <main className="dh-page">{children}</main>
    </>
  );
}
