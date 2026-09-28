import {
  Menu,
  MenuButton,
  MenuGroup,
  MenuGroupHeader,
  MenuItemLink,
  MenuList,
  MenuPopover,
  MenuTrigger,
} from '@fluentui/react-components';
import { ArrowRouting20Regular, ArrowSync20Regular, Flowchart20Regular, Open16Regular } from '@fluentui/react-icons';
import { nimbusEndpointUrl } from '../config';

/**
 * Deep links into nimbus-ops for this account's session: every message about the account flows
 * through one ordered session keyed by the account id.
 */
export function IntegrationTrailMenu({ accountId }: { accountId: string }) {
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <MenuButton appearance="subtle" icon={<Flowchart20Regular />} data-testid="integration-trail">
          Integration trail
        </MenuButton>
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          <MenuGroup>
            <MenuGroupHeader>Open in NimBus (new tab)</MenuGroupHeader>
            <MenuItemLink
              href={nimbusEndpointUrl('BusinessCentralEndpoint', accountId)}
              target="_blank"
              rel="noopener noreferrer"
              icon={<ArrowRouting20Regular />}
              secondaryContent={<Open16Regular />}
            >
              Changes sent to Business Central
            </MenuItemLink>
            <MenuItemLink
              href={nimbusEndpointUrl('D365SalesEndpoint', accountId)}
              target="_blank"
              rel="noopener noreferrer"
              icon={<ArrowSync20Regular />}
              secondaryContent={<Open16Regular />}
            >
              Updates from Business Central
            </MenuItemLink>
          </MenuGroup>
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}
