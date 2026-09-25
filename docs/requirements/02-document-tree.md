# Document tree & node types

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

- FR-T1 A document version contains a tree of nodes of **arbitrary depth**, with arbitrary titles, e.g.
  ```
  Chapter 1
    Section 1
    Section 2
      Subsection 1
      Subsection 2
  Chapter 2
    Section 1
      Subsection 1
      Subsection 2
  ```
- FR-T2 API supports building the hierarchy: add node (as child / at position), rename, change type, move (re-parent / reorder), delete (with subtree).
- FR-T3 Each node has a **type** from the editable node-type dictionary.
- FR-T4 Each node can hold **styled rich text that reproduces Word formatting as closely as possible**: paragraph/character styles (Normal, Heading 1…6, …), fonts, sizes, colors, highlight, alignment, indents, spacing, line spacing, numbered/bulleted lists, tables with merged cells, column widths, borders and shading. Format decision: [content-format.md](../content-format.md).
- FR-T6 A **style catalog** (Word-compatible style ids) is an admin-editable dictionary.
- FR-T5 **[A]** Numbering ("1.2.1") is computed for display only, from the position in the tree; it is not stored.

### 2.3 Node types dictionary
- FR-N1 CRUD for node types (**admins only**) (code, name, description, sort order, active flag).
- FR-N2 **[A]** A node type used by any node cannot be deleted, only deactivated. Deactivated types cannot be used for new nodes.
