# Virtual folders

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

- FR-F1 Folders form a hierarchy of arbitrary depth with arbitrary names.
- FR-F2 CRUD for folders: create, rename, move (re-parent, with cycle protection), delete.
- FR-F3 Documents are attached to a folder; a document can be moved to another folder.
- FR-F5 Folders are managed (create/rename/move/delete) **by administrators only**; all users see the folder tree and create documents in folders.
- FR-F4 **[A]** Folder names are unique among siblings. A folder can be deleted only if it has no sub-folders and no non-deleted documents.
