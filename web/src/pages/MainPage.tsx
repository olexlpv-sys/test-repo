import { Grid, Text } from '@mantine/core';
import { useSearchParams } from 'react-router-dom';
import { useSession } from '../app/sessionContext';
import { DocumentList } from '../components/DocumentList';
import { FolderTree } from '../components/FolderTree';
import { useFolderTree } from '../api/queries';
import type { Schemas } from '../api/client';

function find(nodes: Schemas['FolderNode'][], id: number): Schemas['FolderNode'] | null {
  for (const node of nodes) {
    if (node.id === id) {
      return node;
    }

    const found = find(node.children, id);
    if (found) {
      return found;
    }
  }

  return null;
}

/** The main window (FR-UI1): folder tree left, the selected folder's documents right; the folder is in the URL (?folder=12). */
export function MainPage() {
  const { me } = useSession();
  const [params, setParams] = useSearchParams();
  const folders = useFolderTree();
  const requested = Number(params.get('folder'));
  const selected =
    (Number.isInteger(requested) && requested > 0 && folders.data ? find(folders.data, requested) : null) ??
    folders.data?.[0] ??
    null;

  return (
    <Grid gap={20}>
      <Grid.Col span={{ base: 12, sm: 4, lg: 3 }}>
        <FolderTree
          selectedId={selected?.id ?? null}
          onSelect={(id) => setParams({ folder: String(id) })}
          manage={me?.isAdmin ?? false}
        />
      </Grid.Col>
      <Grid.Col span={{ base: 12, sm: 8, lg: 9 }}>
        {selected ? (
          <DocumentList folderId={selected.id} folderName={selected.name} />
        ) : (
          <Text className="dh-muted">{folders.isSuccess ? 'No folders yet — an admin creates them.' : 'Loading…'}</Text>
        )}
      </Grid.Col>
    </Grid>
  );
}
