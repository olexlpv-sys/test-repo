import { render } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { App } from '../app/App';

/** The whole app at a URL, with a fresh query client that doesn't retry (errors surface at once). */
export function renderApp(url = '/') {
  window.history.pushState({}, '', url);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } } });
  return render(<App queryClient={queryClient} env="test" />);
}
