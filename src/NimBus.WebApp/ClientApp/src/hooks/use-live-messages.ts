import { useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";

export function messageKey(message: api.Message): string {
  return JSON.stringify([
    message.messageId,
    message.endpointId,
    message.endpointRole,
  ]);
}

/** Poll the latest stored messages; the initial page is history, not new traffic. */
export function useLiveMessages(eventType: string, paused: boolean) {
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);
  const [messages, setMessages] = useState<api.Message[]>([]);
  const [traffic, setTraffic] = useState<api.Message[]>([]);
  const [error, setError] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<number>();
  const seen = useRef(new Set<string>());
  const baseline = useRef(false);

  useEffect(() => {
    seen.current.clear();
    baseline.current = false;
    setMessages([]);
    setTraffic([]);
    setUpdatedAt(undefined);
    setError(false);
  }, [eventType]);

  useEffect(() => {
    let active = true;
    let timer: ReturnType<typeof setTimeout>;
    setTraffic([]);
    if (paused) return;

    const refresh = async () => {
      try {
        const response = await client.postMessagesSearch(
          new api.MessageSearchRequest({
            maxItemCount: 100,
            filter: new api.MessageSearchFilter({
              eventTypeId: eventType ? [eventType] : undefined,
            }),
          }),
        );
        if (!active) return;
        const rows = (response.messages ?? []).filter((m) => m.messageId);
        const unique = Array.from(
          new Map(rows.map((m) => [messageKey(m), m])).values(),
        );
        setTraffic(
          baseline.current
            ? unique
                .filter((m) => !seen.current.has(messageKey(m)))
                .slice(0, 60)
            : [],
        );
        for (const row of unique) seen.current.add(messageKey(row));
        // Retain a bounded overlap across polls without growing for the lifetime of the page.
        seen.current = new Set(Array.from(seen.current).slice(-1000));
        baseline.current = true;
        setMessages(unique);
        setUpdatedAt(Date.now());
        setError(false);
      } catch {
        if (active) {
          setError(true);
          setTraffic([]);
        }
      } finally {
        if (active) timer = setTimeout(() => void refresh(), 5000);
      }
    };
    void refresh();
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, [client, eventType, paused]);

  return { messages, traffic, error, updatedAt };
}
