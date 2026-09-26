import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';
import { ApiError, describeError } from '../api/client';

function toast(error: unknown): void {
  const { title, message } = describeError(error);
  notifications.show({ color: 'red', title, message });
}

/** Server state; every failed request shows a toast with the ProblemDetails title and detail. */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    // Queries that handle their errors themselves set meta.silent.
    queryCache: new QueryCache({ onError: (error, query) => !query.meta?.silent && toast(error) }),
    mutationCache: new MutationCache({
      // Mutations that handle a specific error themselves set meta.silent.
      onError: (error, _variables, _context, mutation) => {
        if (!mutation.meta?.silent) {
          toast(error);
        }
      },
    }),
    defaultOptions: {
      queries: {
        retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2,
        refetchOnWindowFocus: false,
      },
    },
  });
}
