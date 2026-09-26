import { useCallback, useMemo, useState, type ReactNode } from 'react';
import { Center, Loader, Text } from '@mantine/core';
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';
import { api, ApiError, describeError, setActingUserId, unwrap, type Schemas } from '../api/client';
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

function remove(key: string): void {
  try {
    localStorage.removeItem(key);
  } catch {
    // Storage unavailable: nothing was stored.
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

  const forgetRecent = useCallback((id: number) => {
    setRecent((previous) => {
      const next = previous.filter((r) => r !== id);
      write(RecentKey, JSON.stringify(next));
      return next;
    });
  }, []);

  // The API confirms the user first: a deactivated or removed one (e.g. still in the recent list) is dropped with a message
  // instead of every screen failing with 401.
  const actAs = useCallback(
    async (id: number) => {
      let confirmed: Schemas['MeResponse'];
      try {
        confirmed = await unwrap(api.GET('/api/me', { headers: { 'X-User-Id': String(id) } }));
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) {
          forgetRecent(id);
          notifications.show({
            color: 'red',
            title: 'User not available',
            message: 'The user is inactive or no longer exists.',
          });
        } else {
          notifications.show({ color: 'red', ...describeError(error) });
        }

        return;
      }

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
      queryClient.setQueryData(['me', id], confirmed);
    },
    [queryClient, forgetRecent],
  );

  // Until a user is chosen, the API's default test user acts (set synchronously so every request carries the header).
  const effectiveUserId = testMode ? (userId ?? info.data?.defaultUserId ?? null) : null;
  setActingUserId(effectiveUserId);

  const me = useQuery({
    queryKey: ['me', effectiveUserId],
    queryFn: () => unwrap(api.GET('/api/me')),
    enabled: effectiveUserId !== null || (info.isSuccess && !testMode),
    meta: { silent: true },
    placeholderData: keepPreviousData,
  });

  // The stored user was deactivated or removed (401): forget it and act as the default user again.
  if (testMode && userId !== null && me.error instanceof ApiError && me.error.status === 401) {
    setUserId(null);
    remove(StorageKey);
  }

  const value = useMemo<Session>(
    () => ({ testMode, userId: effectiveUserId, me: me.data, recentUserIds, actAs }),
    [testMode, effectiveUserId, me.data, recentUserIds, actAs],
  );
  // Nothing asks the API before it is known who acts and that the API accepts them (test mode) — otherwise every request
  // would fail with 401. Switching keeps the previous user's data on screen until the new one is confirmed.
  if (!info.isSuccess || (testMode && !me.isSuccess)) {
    const failure = info.isError
      ? 'The API is not reachable.'
      : me.isError && userId === null
        ? describeError(me.error).message
        : null;
    return <Center h="100vh">{failure ? <Text c="red">{failure}</Text> : <Loader />}</Center>;
  }

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}
