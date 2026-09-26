import type { SignatureStatus, VersionHeader } from './api';

/** "Draft (based on v2)" or the version's own label. */
export function versionLabel(v: VersionHeader, versions: VersionHeader[]): string {
  const base = versions.find((x) => x.id === v.basedOnVersionId)?.label;
  return v.status === 'Draft' ? `Draft${base ? ` (based on ${base})` : ''}` : v.label;
}

type ApproverState = 'signed' | 'outdated' | 'pending';

/** Each required approver: ✔ signed · ⚠ outdated (the draft changed after signing) · ⏳ pending. */
export function approverStates(
  status: SignatureStatus,
): { id: number; name: string; state: ApproverState; comment: string | null }[] {
  return status.requiredApprovers.map((approver) => {
    const signature = status.signatures.find((s) => s.user.id === approver.id);
    return {
      id: approver.id,
      name: approver.displayName,
      state: signature ? (signature.isValid ? 'signed' : 'outdated') : 'pending',
      comment: signature?.comment ?? null,
    };
  });
}
