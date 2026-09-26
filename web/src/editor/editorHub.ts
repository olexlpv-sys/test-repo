import { createContext, useContext } from 'react';
import type { Editor } from '@tiptap/core';

/** The shared ribbon acts on the focused section's editor (T15 §3). */
export interface EditorHub {
  active: Editor | null;
  setActive: (editor: Editor | null) => void;
  /** Versions whose "editing outdates signatures" warning the user already confirmed in this session. */
  warned: Set<number>;
}

export const EditorHubContext = createContext<EditorHub>({ active: null, setActive: () => {}, warned: new Set() });

export const useEditorHub = () => useContext(EditorHubContext);
