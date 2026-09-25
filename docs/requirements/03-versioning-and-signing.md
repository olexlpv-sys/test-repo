# Document lifecycle, versioning & signing

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

- FR-V1 Statuses: `Draft`, `Signed`, `Deleted`.
- FR-V2 A signed draft becomes the next version number: v1, v2, v3…
- FR-V3 A new `Draft` version is created from the **latest signed version** (deep copy of tree and content). **[A]** At most one draft per document at any time.
- FR-V4 **Only Draft versions are editable.** Any modification of a Signed/Deleted version or of a deleted document via the API is rejected.
- FR-V5 **[A]** `Deleted` applies to (a) a whole document (soft delete) and (b) a discarded draft version. Signed versions are never deleted through the API.
- FR-V8 A deleted document can be **restored** via the API (owner or admin), optionally into another folder. Deleting a document does not change its versions, so a restore returns it exactly as it was.
- FR-V6 **Signing is done by Approvers — all of them must sign.** Each approver signs the draft individually; the draft becomes `Signed` (vN) when **every** current approver has a valid signature. A signature is bound to the content hash: any later change of the draft makes earlier signatures **outdated** and they must sign again **[A]**. An approver can withdraw their signature before the version is finalized. The owner cannot be an Approver. A document without Approvers cannot be signed; removing the last approver never signs a draft.
- FR-V7 **[A]** Only the document owner can create a new draft, discard a draft, rename and delete the document; owner or admin can move it. Renaming requires an open draft (A-1). A draft can be discarded only if a signed version exists; otherwise the document is deleted instead (A-2).
