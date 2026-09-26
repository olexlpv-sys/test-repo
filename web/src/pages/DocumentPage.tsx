import { Anchor, Badge, Group, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { api, unwrap } from '../api/client';

/** The document form. The section editor, versions bar and history arrive with T15; this shows the document header. */
export function DocumentPage() {
  const id = Number(useParams().id);
  const document = useQuery({
    queryKey: ['document', id],
    queryFn: () => unwrap(api.GET('/api/documents/{id}', { params: { path: { id } } })),
    enabled: Number.isInteger(id),
  });
  const d = document.data;
  return (
    <Stack>
      <Anchor component={Link} to={d ? `/?folder=${d.folderId}` : '/'} size="sm">
        ← Back to the list
      </Anchor>
      {d && (
        <>
          <Group>
            <Title order={2}>{d.title}</Title>
            <Badge>{d.status}</Badge>
          </Group>
          <Text c="dimmed" size="sm">
            Owner {d.owner.displayName} · {d.versions.map((v) => v.label).join(' · ')}
          </Text>
          <Text size="sm">The section editor comes with the next queue item (T15).</Text>
        </>
      )}
    </Stack>
  );
}
