import { useQuery } from '@tanstack/react-query';
import { api, unwrap } from './client';

/** The folder tree (shared by the main window, pickers and the Admin tab). */
export function useFolderTree() {
  return useQuery({ queryKey: ['folders', 'tree'], queryFn: () => unwrap(api.GET('/api/folders/tree')) });
}
