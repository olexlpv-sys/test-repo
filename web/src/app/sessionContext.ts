import { createContext, useContext } from 'react';
import type { Schemas } from '../api/client';

export interface Session {
  /** Test auth mode: the header shows the "Acting as" switcher and a TEST MODE banner (FR-UI4). */
  testMode: boolean;
  /** The acting user's id (null until known). */
  userId: number | null;
  me: Schemas['MeResponse'] | undefined;
  recentUserIds: number[];
  /** Switch to a user after the API confirmed them (an unavailable user is dropped from the recent list with a message). */
  actAs: (userId: number) => Promise<void>;
}

export const SessionContext = createContext<Session | null>(null);

export function useSession(): Session {
  const session = useContext(SessionContext);
  if (!session) {
    throw new Error('useSession outside SessionProvider');
  }

  return session;
}
