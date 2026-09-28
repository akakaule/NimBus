import { PageBody, Panel } from '../components/Layout';
import { PageHeader } from '../components/PageHeader';
import { RouterLink } from '../components/RouterLink';

/** Unknown route inside the BC client. */
export default function NotFound() {
  return (
    <>
      <PageHeader title="Page not found" />
      <PageBody>
        <Panel padded>
          <p>
            This page doesn't exist in the Business Central (simulated) client. <RouterLink to="/">Go to the Role Center</RouterLink>.
          </p>
        </Panel>
      </PageBody>
    </>
  );
}
