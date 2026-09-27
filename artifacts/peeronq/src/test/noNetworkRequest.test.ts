import { describe, it, expect } from 'vitest';
import { apiClient } from '../services/apiClient';
import { isNotConfigured } from '../types';

describe('apiClient', () => {
  it('truthfully reports that the offline prototype has no API transport', async () => {
    const result = await apiClient.ping();
    expect(isNotConfigured(result)).toBe(true);
    if (isNotConfigured(result)) {
      expect(result.reason).toBe(
        'The offline prototype has no configured API transport',
      );
    }
  });
});

describe('offline product invariant', () => {
  const productSources = import.meta.glob('../**/*.{ts,tsx}', {
    eager: true,
    import: 'default',
    query: '?raw',
  }) as Record<string, string>;

  const networkPrimitivePatterns = [
    new RegExp(['fet', 'ch\\s*\\('].join(''), 'u'),
    new RegExp(['XMLHttp', 'Request'].join(''), 'u'),
    new RegExp(['new\\s+Web', 'Socket\\s*\\('].join(''), 'u'),
    new RegExp(['Event', 'Source\\s*\\('].join(''), 'u'),
    new RegExp(['send', 'Beacon\\s*\\('].join(''), 'u'),
  ];

  it('contains no browser network primitives outside tests and vendored UI', () => {
    const violations = Object.entries(productSources)
      .filter(
        ([path]) =>
          !path.includes('/test/') && !path.includes('/components/ui/'),
      )
      .flatMap(([path, source]) =>
        networkPrimitivePatterns
          .filter((pattern) => pattern.test(source))
          .map((pattern) => `${path}: ${pattern.source}`),
      );

    expect(violations).toEqual([]);
  });
});
