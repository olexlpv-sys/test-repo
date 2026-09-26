import { useMemo, useState } from 'react';
import { Select } from '@mantine/core';
import { useDebouncedValue, useHotkeys } from '@mantine/hooks';
import { useQuery } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../api/client';
import { useSession } from '../app/sessionContext';

type User = Schemas['UserResponse'];

const labelOf = (u: Pick<User, 'displayName' | 'login' | 'isAdmin'>) =>
  `${u.displayName} (${u.login})${u.isAdmin ? ' · admin' : ''}`;

/**
 * "Acting as" (FR-UI4): type-ahead over the user directory plus the last used users; the choice is kept in localStorage and
 * switching reloads all data with the new user's rights. Ctrl+Shift+U cycles through the recent users.
 */
export function ActingAs() {
  const { userId, recentUserIds, actAs, me } = useSession();
  const [search, setSearch] = useState('');
  const [debounced] = useDebouncedValue(search, 250);
  // After a choice the Select shows the chosen user's label as its search text; that is not a search.
  // Switching also leaves the previous user's label in the (debounced) search for a moment.
  const [shownLabels, setShownLabels] = useState<string[]>([]);
  const term = shownLabels.includes(debounced) || (me && debounced === labelOf(me)) ? '' : debounced;

  const found = useQuery({
    queryKey: ['users', 'search', term],
    queryFn: () => unwrap(api.GET('/api/users', { params: { query: { Search: term || undefined, PageSize: 20 } } })),
  });
  const recent = useQuery({
    queryKey: ['users', 'recent', recentUserIds],
    queryFn: async () =>
      Promise.all(
        recentUserIds.map((id) => unwrap(api.GET('/api/users/{id}', { params: { path: { id } } })).catch(() => null)),
      ),
    enabled: recentUserIds.length > 0,
  });

  const users = useMemo(() => {
    const byId = new Map<number, User>();
    for (const user of [...(recent.data ?? []), ...(found.data?.items ?? [])]) {
      if (user && user.isActive) {
        byId.set(user.id, user);
      }
    }

    return [...byId.values()];
  }, [recent.data, found.data]);

  useHotkeys([
    [
      'mod+shift+U',
      () => {
        if (recentUserIds.length > 1 && userId !== null) {
          const next = recentUserIds[(recentUserIds.indexOf(userId) + 1) % recentUserIds.length];
          if (next !== undefined) {
            void actAs(next);
          }
        }
      },
    ],
  ]);

  const recentSet = new Set(recentUserIds);
  const data = [
    { group: 'Recent', items: users.filter((u) => recentSet.has(u.id)) },
    { group: 'Users', items: users.filter((u) => !recentSet.has(u.id)) },
  ]
    .filter((g) => g.items.length > 0)
    .map((g) => ({
      group: g.group,
      items: g.items.map((u) => ({
        value: String(u.id),
        label: `${u.displayName} (${u.login})${u.isAdmin ? ' · admin' : ''}`,
      })),
    }));

  return (
    <Select
      aria-label="Acting as"
      w={300}
      searchable
      data={data}
      value={userId === null ? null : String(userId)}
      onChange={(value, option) => {
        if (value) {
          setShownLabels(me ? [labelOf(me), option.label] : [option.label]);
          void actAs(Number(value));
        }
      }}
      searchValue={search}
      onSearchChange={setSearch}
      nothingFoundMessage="No users"
      placeholder={me ? me.displayName : 'Acting as…'}
      comboboxProps={{ withinPortal: true }}
    />
  );
}
