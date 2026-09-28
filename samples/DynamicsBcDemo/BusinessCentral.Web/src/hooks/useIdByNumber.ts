import { useEffect, useState } from 'react';
import { api } from '../api';

interface NumberedRecord {
  id: string;
  number: string;
}

/**
 * Finds the id behind a document number the API only returns as text (a quote's Order No., an
 * order's Quote No.), so the number can link to its card. Undefined until found.
 */
export function useIdByNumber(kind: 'order' | 'quote', number: string | null | undefined): string | undefined {
  const [found, setFound] = useState<NumberedRecord | null>(null);

  useEffect(() => {
    if (!number) return;
    const controller = new AbortController();
    const load: Promise<NumberedRecord[]> =
      kind === 'order' ? api.orders(controller.signal) : api.quotes(controller.signal);
    load
      .then((records) => {
        const record = records.find((r) => r.number === number);
        if (record) setFound({ id: record.id, number: record.number });
      })
      .catch(() => {
        // Without the id the number still shows, just not as a link.
      });
    return () => controller.abort();
  }, [kind, number]);

  return found && found.number === number ? found.id : undefined;
}
