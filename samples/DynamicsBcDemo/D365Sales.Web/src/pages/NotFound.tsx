import { DocumentOnePage20Regular } from '@fluentui/react-icons';
import { EmptyState } from '../components/PageStates';
import { Card, RecordLink } from '../components/ui';

export function NotFound() {
  return (
    <Card>
      <EmptyState icon={<DocumentOnePage20Regular />} title="This page doesn't exist">
        Go to the <RecordLink to="/">dashboard</RecordLink>.
      </EmptyState>
    </Card>
  );
}
