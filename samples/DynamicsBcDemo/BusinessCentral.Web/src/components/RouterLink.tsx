import { Link } from '@fluentui/react-components';
import type { MouseEvent, ReactNode } from 'react';
import { useHref, useLinkClickHandler } from 'react-router-dom';

interface RouterLinkProps {
  to: string;
  children: ReactNode;
  className?: string;
  appearance?: 'default' | 'subtle';
  title?: string;
  'data-testid'?: string;
}

/** A Fluent link that navigates inside the SPA and still opens a new tab on Ctrl/middle click. */
export function RouterLink({ to, children, ...props }: RouterLinkProps) {
  const href = useHref(to);
  const handleClick = useLinkClickHandler<HTMLAnchorElement>(to);
  return (
    <Link
      {...props}
      href={href}
      onClick={(event: MouseEvent<HTMLAnchorElement>) => {
        if (!event.defaultPrevented) handleClick(event);
      }}
    >
      {children}
    </Link>
  );
}
