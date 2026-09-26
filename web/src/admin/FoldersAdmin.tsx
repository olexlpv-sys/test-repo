import { useState } from 'react';
import { Grid, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { api, unwrap } from '../api/client';
import { DocumentList } from '../components/DocumentList';
import { FolderTree } from '../components/FolderTree';

/** Folders (FR-F5): the main window's tree with full management, the folder's details and all its documents (deleted too). */
export function FoldersAdmin() {
  const [selected, setSelected] = useState<number | null>(null);
  const folder = useQuery({
    queryKey: ['folders', 'details', selected ?? 0],
    queryFn: () => unwrap(api.GET('/api/folders/{id}', { params: { path: { id: selected ?? 0 } } })),
    enabled: selected !== null,
    meta: { silent: true },
  });
  const all = useQuery({
    queryKey: ['documents', 'admin-count', selected ?? 0],
    queryFn: () =>
      unwrap(
        api.GET('/api/folders/{folderId}/documents', {
          params: { path: { folderId: selected ?? 0 }, query: { IncludeDeleted: true, PageSize: 1 } },
        }),
      ),
    enabled: selected !== null,
  });

  return (
    <Grid gap={20}>
      <Grid.Col span={{ base: 12, md: 4 }}>
        <FolderTree
          selectedId={selected}
          onSelect={setSelected}
          onDeleted={(id) => id === selected && setSelected(null)}
          manage
        />
      </Grid.Col>
      <Grid.Col span={{ base: 12, md: 8 }}>
        {selected === null ? (
          <Text className="dh-muted">Select a folder to see its details and documents.</Text>
        ) : (
          <>
            {folder.data && (
              <div className="dh-card" style={{ marginBottom: 20 }} data-testid="folder-details">
                <div className="dh-card-body">
                  <div className="dh-field-label" style={{ textAlign: 'left' }}>
                    Path
                  </div>
                  <div className="dh-field-value" style={{ textAlign: 'left' }}>
                    {folder.data.path.map((p) => p.name).join(' / ')}
                  </div>
                  <div className="dh-field-label" style={{ textAlign: 'left', marginTop: 12 }}>
                    Documents (deleted included)
                  </div>
                  <div className="dh-field-value" style={{ textAlign: 'left' }}>
                    {all.data?.totalCount ?? '…'}
                  </div>
                </div>
              </div>
            )}
            <DocumentList key={selected} folderId={selected} folderName={folder.data?.name ?? ''} initialShowDeleted />
          </>
        )}
      </Grid.Col>
    </Grid>
  );
}
