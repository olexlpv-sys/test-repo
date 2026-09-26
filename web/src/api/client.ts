import createClient from 'openapi-fetch';
import type { components, paths } from './schema';

export type Schemas = components['schemas'];

/** A ProblemDetails answer of the API (docs/architecture.md: error codes). */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly type: string,
    readonly title: string,
    readonly detail?: string,
    readonly errors?: Record<string, string[]>,
  ) {
    super(detail ?? title);
    this.name = 'ApiError';
  }
}

let actingUserId: number | null = null;

/** The user sent as X-User-Id (test auth mode, FR-UI4). */
export function setActingUserId(id: number | null): void {
  actingUserId = id;
}

// fetch is looked up per request (tests replace it).
export const api = createClient<paths>({
  baseUrl: import.meta.env.VITE_API_BASE_URL ?? '',
  fetch: (request) => globalThis.fetch(request),
});

api.use({
  onRequest({ request }) {
    if (actingUserId !== null) {
      request.headers.set('X-User-Id', String(actingUserId));
    }

    return request;
  },
});

interface ProblemBody {
  type?: string;
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
}

function toApiError(response: Response, body: unknown): ApiError {
  const problem = (typeof body === 'object' && body !== null ? body : {}) as ProblemBody;
  return new ApiError(
    response.status,
    problem.type ?? 'request-failed',
    problem.title ?? response.statusText,
    problem.detail,
    problem.errors,
  );
}

/** The data of an openapi-fetch call, or an ApiError for a non-success answer. */
export async function unwrap<T>(call: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await call;
  if (!response.ok || error !== undefined) {
    throw toApiError(response, error);
  }

  return data as T;
}

/** A human message for an error: concurrency conflicts get the reload hint (T14). */
export function describeError(error: unknown): { title: string; message: string } {
  if (error instanceof ApiError) {
    if (error.type === 'concurrency-conflict') {
      return { title: 'Changed by someone else', message: 'Reload and try again.' };
    }

    const fieldErrors = error.errors ? Object.values(error.errors).flat().join(' ') : '';
    return { title: error.title, message: error.detail ?? (fieldErrors || `Request failed (${error.status}).`) };
  }

  return { title: 'Request failed', message: error instanceof Error ? error.message : String(error) };
}
