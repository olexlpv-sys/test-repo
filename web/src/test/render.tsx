import { render } from '@testing-library/react';
import { App } from '../app/App';
import { createQueryClient } from '../app/queryClient';

/** The whole app at a URL, with the app's query client (error toasts) minus retries, so errors surface at once. */
export function renderApp(url = '/') {
  window.history.pushState({}, '', url);
  const queryClient = createQueryClient();
  queryClient.setDefaultOptions({ queries: { ...queryClient.getDefaultOptions().queries, retry: false } });
  return render(<App queryClient={queryClient} env="test" />);
}
