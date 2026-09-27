import { describe, it, expect } from 'vitest';
import { deviceIdSchema } from '../lib/validation';

describe('deviceIdSchema', () => {
  it('normalizes pasted values with spaces', () => {
    const result = deviceIdSchema.safeParse(' 123 456 789 012 ');
    expect(result.success).toBe(true);
    if (result.success) {
      expect(result.data).toBe('123-456-789-012');
    }
  });
  
  it('normalizes internal spaces', () => {
    const result = deviceIdSchema.safeParse('123456789012');
    expect(result.success).toBe(true);
    if (result.success) {
      expect(result.data).toBe('123-456-789-012');
    }
  });

  it('valid ID format passes', () => {
    const result = deviceIdSchema.safeParse('999-888-777-666');
    expect(result.success).toBe(true);
  });

  it('invalid format fails', () => {
    const result = deviceIdSchema.safeParse('12-34-56-78');
    expect(result.success).toBe(false);
  });

  it('rejects the retired prefix in user input', () => {
    expect(deviceIdSchema.safeParse('LNK-123-456-789-012').success).toBe(false);
  });
});
