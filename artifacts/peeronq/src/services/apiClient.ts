import { NotConfigured } from '@/types';

interface ApiClient {
  ping(): Promise<boolean | NotConfigured>;
}

function createApiClient(): ApiClient {
  const notConfigured: NotConfigured = {
    _type: 'NotConfigured',
    reason: 'The offline prototype has no configured API transport',
  };
  return {
    ping: () => Promise.resolve(notConfigured),
  };
}

export const apiClient = createApiClient();
