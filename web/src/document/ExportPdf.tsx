import { useState } from 'react';
import { Button, Checkbox, Group, Modal, Radio, SegmentedControl, Stack, Text } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { api, unwrap } from '../api/client';
import type { TreeNode, VersionHeader } from './api';
import { trackExport } from './exportJobs';

interface Props {
  opened: boolean;
  onClose: () => void;
  version: VersionHeader;
  /** The selected section (offered as the scope, preselected when {@link sectionFirst}). */
  section: TreeNode | null;
  sectionFirst: boolean;
}

/** The "Export PDF" dialog (T20 UI): scope, page size and the parts to include. */
export function ExportPdfDialog({ opened, onClose, version, section, sectionFirst }: Props) {
  return (
    <Modal opened={opened} onClose={onClose} title="Export PDF">
      {/* Remounted per opening, so it starts from the current selection. */}
      {opened && <ExportForm version={version} section={section} sectionFirst={sectionFirst} onClose={onClose} />}
    </Modal>
  );
}

function ExportForm({ version, section, sectionFirst, onClose }: Omit<Props, 'opened'>) {
  const [scope, setScope] = useState<'document' | 'section'>(section && sectionFirst ? 'section' : 'document');
  const [pageSize, setPageSize] = useState<'A4' | 'Letter'>('A4');
  const [titlePage, setTitlePage] = useState(true);
  const [toc, setToc] = useState(true);
  const [headerFooter, setHeaderFooter] = useState(true);
  const [signaturePage, setSignaturePage] = useState(true);
  const signed = version.status === 'Signed';
  const target = scope === 'section' && section ? section : null;

  const start = useMutation({
    mutationFn: () =>
      unwrap(
        api.POST('/api/versions/{versionId}/exports/pdf', {
          params: { path: { versionId: version.id } },
          body: { logicalNodeId: target?.logicalNodeId ?? null, pageSize, titlePage, toc, headerFooter, signaturePage },
        }),
      ),
    onSuccess: (job) => {
      onClose();
      void trackExport(job, target ? `${target.number} ${target.title}` : version.label);
    },
  });

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        start.mutate();
      }}
    >
      <Stack>
        <Radio.Group label="Export" value={scope} onChange={(v) => setScope(v as 'document' | 'section')}>
          <Stack gap={6} mt={6}>
            <Radio value="document" label={`Whole document (${version.label})`} />
            <Radio
              value="section"
              disabled={!section}
              label={
                section
                  ? `Section ${section.number} ${section.title} with its subsections`
                  : 'Selected section (select one first)'
              }
            />
          </Stack>
        </Radio.Group>
        <div>
          <Text size="sm" fw={500} mb={4}>
            Page size
          </Text>
          <SegmentedControl
            value={pageSize}
            onChange={(v) => setPageSize(v as 'A4' | 'Letter')}
            data={['A4', 'Letter']}
            aria-label="Page size"
          />
        </div>
        <Stack gap={8}>
          <Checkbox label="Title page" checked={titlePage} onChange={(e) => setTitlePage(e.currentTarget.checked)} />
          <Checkbox label="Table of contents" checked={toc} onChange={(e) => setToc(e.currentTarget.checked)} />
          <Checkbox
            label="Header and footer (page X of Y)"
            checked={headerFooter}
            onChange={(e) => setHeaderFooter(e.currentTarget.checked)}
          />
          <Checkbox
            label="Signature page"
            description={signed ? undefined : 'Signed versions only'}
            disabled={!signed}
            checked={signed && signaturePage}
            onChange={(e) => setSignaturePage(e.currentTarget.checked)}
          />
        </Stack>
        {version.status === 'Draft' && (
          <Text size="xs" className="dh-muted">
            Drafts are marked with a DRAFT watermark.
          </Text>
        )}
      </Stack>
      <Group className="dh-modal-footer" gap={12}>
        <Button variant="soft" color="gray" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={start.isPending}>
          Export
        </Button>
      </Group>
    </form>
  );
}
