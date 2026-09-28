import { useEffect } from 'react';

/** Names the browser tab, so the presenter can tell the demo tabs apart. */
export function useDocumentTitle(title: string) {
  useEffect(() => {
    const previous = document.title;
    document.title = title;
    return () => {
      document.title = previous;
    };
  }, [title]);
}
