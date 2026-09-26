import { useState } from 'react';
import { MantineProvider } from '@mantine/core';
import { ModalsProvider } from '@mantine/modals';
import { Notifications } from '@mantine/notifications';
import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import { createQueryClient } from './queryClient';
import { SessionProvider } from './session';
import { Shell } from './Shell';
import { MainPage } from '../pages/MainPage';
import { DocumentPage } from '../pages/DocumentPage';
import { ComparePage } from '../pages/ComparePage';
import { AdminPage } from '../pages/AdminPage';
import { theme } from '../theme';

/** Providers and routes (`env="test"`: Mantine without transitions and portals, for component tests): `/` main window, `/documents/:id` the document form (T15), `/admin` (T17). */
export function App({ queryClient, env }: { queryClient?: QueryClient; env?: 'default' | 'test' }) {
  const [client] = useState(() => queryClient ?? createQueryClient());
  return (
    <MantineProvider theme={theme} env={env}>
      <Notifications position="top-right" />
      <QueryClientProvider client={client}>
        <ModalsProvider>
          <SessionProvider>
            <BrowserRouter>
              <Shell>
                <Routes>
                  <Route path="/" element={<MainPage />} />
                  <Route path="/documents/:id" element={<DocumentPage />} />
                  <Route path="/documents/:id/compare" element={<ComparePage />} />
                  <Route path="/admin" element={<AdminPage />} />
                </Routes>
              </Shell>
            </BrowserRouter>
          </SessionProvider>
        </ModalsProvider>
      </QueryClientProvider>
    </MantineProvider>
  );
}
