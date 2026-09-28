import { useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../api';
import { noticeState } from './useRouteNotice';

/**
 * "Create sales quote" for a CRM opportunity: Business Central creates a draft quote linked to it
 * (without lines), then the quote card opens with a notice. Errors are left to the caller.
 */
export function useCreateQuote(): (crmOpportunityId: string) => Promise<void> {
  const navigate = useNavigate();
  return useCallback(
    async (crmOpportunityId: string) => {
      const quote = await api.createQuote(crmOpportunityId);
      const opportunity = quote.crmOpportunity;
      navigate(`/quotes/${quote.id}`, {
        state: noticeState({
          intent: 'success',
          title: `Sales quote ${quote.number} created`,
          message:
            `${opportunity ? `Linked to CRM opportunity ${opportunity.number} · ${opportunity.name}. ` : ''}` +
            'NimBus lets Dynamics 365 know. Add lines, then send the quote.',
        }),
      });
    },
    [navigate],
  );
}
