import { Box, Text } from '@mantine/core';
import { useState } from 'react';
import { useSession } from '../app/sessionContext';
import { FolderTree } from '../components/FolderTree';

/** Admin tab (T17 adds node types, styles, users and the audit log); folders are managed with the shared tree. */
export function AdminPage() {
  const { me } = useSession();
  const [selected, setSelected] = useState<number | null>(null);
  if (!me?.isAdmin) {
    return <Text className="dh-muted">The Admin tab is for administrators.</Text>;
  }

  return (
    <Box maw={480}>
      <FolderTree selectedId={selected} onSelect={setSelected} manage />
    </Box>
  );
}
