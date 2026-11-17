/** Helpers live outside migrations/ and are imported with relative paths. */
export function normalise(email) {
  return email.trim().toLowerCase();
}