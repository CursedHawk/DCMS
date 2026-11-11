export const str = (input: Record<string, unknown>, key: string): string | undefined => {
  const value = input[key];
  return typeof value === 'string' && value.trim() ? value.trim() : undefined;
};

export const obj = (input: Record<string, unknown>, key: string): Record<string, unknown> => {
  const value = input[key];
  return value && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
};

export const ids = (input: Record<string, unknown>, key: string): string[] => {
  const value = input[key];
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : [];
};

/** Required-argument accessor: a missing id must fail loudly rather than hit `/content/undefined`. */
export const required = (input: Record<string, unknown>, key: string): string => {
  const value = str(input, key);
  if (!value) throw new Error(`The "${key}" argument is required.`);
  return value;
};

