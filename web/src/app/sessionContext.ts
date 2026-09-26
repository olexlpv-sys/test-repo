import { createContext, useContext } from 'react';
import type { Schemas } from '../api/client';

export interface Session {
  /** Test auth mode: the header shows the "Acting as" switcher and a TEST MODE banner (FR-UI4). */
  testMode: boolean;
  /** The acting user's id (null until known). */
  userId: number | null;
  me: Schemas['MeResponse'] | undefined;
  recentUserIds: number[];
  actAs: (userId: number) => void;
}

export const SessionContext = createContext<Session | null>(null);

export function useSession(): Session {
  const session = useContext(SessionContext);
  if (!session) {
    throw new Error('useSession outside SessionProvider');
  }

  return session;
}
