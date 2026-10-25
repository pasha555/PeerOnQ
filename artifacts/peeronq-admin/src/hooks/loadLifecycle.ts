export function beginOnlineLoad(
  online: boolean,
  setLoading: (loading: boolean) => void,
  clearError: () => void,
): AbortController | null {
  if (!online) {
    setLoading(false);
    return null;
  }
  const controller = new AbortController();
  setLoading(true);
  clearError();
  return controller;
}
