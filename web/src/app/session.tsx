import { useCallback, useMemo, useState, type ReactNode } from 'react';
import { Center, Loader, Text } from '@mantine/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api, setActingUserId, unwrap } from '../api/client';
import { SessionContext, type Session } from './sessionContext';

const StorageKey = 'dochub.actingUserId';
const RecentKey = 'dochub.recentUserIds';
const MaxRecent = 10;

function readNumber(key: string): number | null {
  try {
    const value = Number(localStorage.getItem(key));
    return Number.isInteger(value) && value > 0 ? value : null;
  } catch {
    return null;
  }
}

function readRecent(): number[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(RecentKey) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter((n): n is number => Number.isInteger(n)).slice(0, MaxRecent) : [];
  } catch {
    return [];
  }
}

function write(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Storage may be unavailable (private mode); the selection then lasts for this page only.
  }
}

export function SessionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [userId, setUserId] = useState<number | null>(() => {
    const stored = readNumber(StorageKey);
    setActingUserId(stored);
    return stored;
  });
  const [recentUserIds, setRecent] = useState<number[]>(readRecent);

  const info = useQuery({
    queryKey: ['system-info'],
    queryFn: () => unwrap(api.GET('/api/system/info')),
    staleTime: Infinity,
  });
  const testMode = info.data?.authMode === 'Test';

  const actAs = useCallback(
    (id: number) => {
      setActingUserId(id);
      setUserId(id);
      write(StorageKey, String(id));
      setRecent((previous) => {
        const next = [id, ...previous.filter((r) => r !== id)].slice(0, MaxRecent);
        write(RecentKey, JSON.stringify(next));
        return next;
      });
      // Every screen re-renders with the new user's rights.
      queryClient.removeQueries({ predicate: (q) => q.queryKey[0] !== 'system-info' });
    },
    [queryClient],
  );

  // Until a user is chosen, the API's default test user acts (set synchronously so every request carries the header).
  const effectiveUserId = testMode ? (userId ?? info.data?.defaultUserId ?? null) : null;
  setActingUserId(effectiveUserId);

  const me = useQuery({
    queryKey: ['me', effectiveUserId],
    queryFn: () => unwrap(api.GET('/api/me')),
    enabled: effectiveUserId !== null || (info.isSuccess && !testMode),
  });

  const value = useMemo<Session>(
    () => ({ testMode, userId: effectiveUserId, me: me.data, recentUserIds, actAs }),
    [testMode, effectiveUserId, me.data, recentUserIds, actAs],
  );
  // Nothing asks the API before it is known who acts (test mode) — otherwise every first request would fail with 401.
  if (!info.isSuccess || (testMode && effectiveUserId === null)) {
    return <Center h="100vh">{info.isError ? <Text c="red">The API is not reachable.</Text> : <Loader />}</Center>;
  }

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}
