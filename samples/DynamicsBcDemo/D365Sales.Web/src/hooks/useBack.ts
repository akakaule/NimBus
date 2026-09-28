import { useCallback } from 'react';
import { useNavigate } from 'react-router-dom';

/** The form's back arrow: history back inside the app, otherwise the list it belongs to. */
export function useBack(fallback: string): () => void {
  const navigate = useNavigate();
  return useCallback(() => {
    const index = (window.history.state as { idx?: number } | null)?.idx ?? 0;
    if (index > 0) navigate(-1);
    else navigate(fallback);
  }, [navigate, fallback]);
}
