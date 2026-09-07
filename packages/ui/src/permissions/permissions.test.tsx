import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { MyPermissions } from '@dcms/core';
import { Button } from '../ui/button';
import { TooltipProvider } from '../ui/tooltip';
import { Can } from './Can';
import { PermissionProvider, useCan } from './context';
import { PermissionTooltip } from './PermissionTooltip';
import { RequirePermission } from './RequirePermission';

const holder: MyPermissions = { isSuperAdmin: false, permissions: ['media:read'] };
const superAdmin: MyPermissions = { isSuperAdmin: true, permissions: [] };

function wrap(me: MyPermissions | undefined, ui: React.ReactNode) {
  return render(
    <TooltipProvider>
      <PermissionProvider value={me}>{ui}</PermissionProvider>
    </TooltipProvider>,
  );
}

describe('Can', () => {
  it('renders for a holder', () => {
    wrap(holder, <Can perm="media:read">Upload</Can>);
    expect(screen.getByText('Upload')).toBeInTheDocument();
  });

  it('renders nothing for a non-holder', () => {
    wrap(holder, <Can perm="media:write">Upload</Can>);
    expect(screen.queryByText('Upload')).not.toBeInTheDocument();
  });

  it('renders a fallback where one is given', () => {
    wrap(holder, <Can perm="media:write" fallback={<span>Read only</span>}>Upload</Can>);
    expect(screen.getByText('Read only')).toBeInTheDocument();
  });

  it('lets a SuperAdmin through anything', () => {
    wrap(superAdmin, <Can perm="media:write">Upload</Can>);
    expect(screen.getByText('Upload')).toBeInTheDocument();
  });

  it('renders when no permission is named — an unguarded thing is visible', () => {
    wrap(holder, <Can>Always</Can>);
    expect(screen.getByText('Always')).toBeInTheDocument();
  });

  it('hides a SuperAdmin-only thing from an ordinary holder of every permission', () => {
    wrap({ isSuperAdmin: false, permissions: ['media:read', 'media:write'] },
      <Can superAdmin>Tenants</Can>);
    expect(screen.queryByText('Tenants')).not.toBeInTheDocument();
  });

  it('denies while permissions are still loading', () => {
    // Defaulting open would flash controls that then vanish, which reads as a bug.
    wrap(undefined, <Can perm="media:read">Upload</Can>);
    expect(screen.queryByText('Upload')).not.toBeInTheDocument();
  });
});

function Probe({ perm }: { perm: string }) {
  return <span>{useCan(perm) ? 'yes' : 'no'}</span>;
}

describe('useCan', () => {
  it('answers for the ambient caller', () => {
    wrap(holder, <Probe perm="media:read" />);
    expect(screen.getByText('yes')).toBeInTheDocument();
  });

  it('answers no outside any provider rather than throwing', () => {
    // A component rendered in isolation — a test, a storybook — should not explode.
    render(<Probe perm="media:read" />);
    expect(screen.getByText('no')).toBeInTheDocument();
  });
});

describe('RequirePermission', () => {
  it('renders the page for a holder', () => {
    wrap(holder, <RequirePermission perm="media:read"><h1>Media</h1></RequirePermission>);
    expect(screen.getByRole('heading', { name: 'Media' })).toBeInTheDocument();
  });

  it('refuses a non-holder and names the permission they need', () => {
    wrap(holder, <RequirePermission perm="media:write"><h1>Media</h1></RequirePermission>);
    expect(screen.queryByRole('heading', { name: 'Media' })).not.toBeInTheDocument();
    expect(screen.getByText('media:write')).toBeInTheDocument();
  });

  it('waits rather than accusing while permissions load', () => {
    // Flashing "you do not have access" at someone who does is worse than a moment of nothing.
    wrap(undefined, <RequirePermission perm="media:read"><h1>Media</h1></RequirePermission>);
    expect(screen.queryByText(/do not have access/)).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Media' })).not.toBeInTheDocument();
  });

  it('gates a SuperAdmin-only page', () => {
    wrap(holder, <RequirePermission superAdmin><h1>Tenants</h1></RequirePermission>);
    expect(screen.queryByRole('heading', { name: 'Tenants' })).not.toBeInTheDocument();
    wrap(superAdmin, <RequirePermission superAdmin><h1>Tenants</h1></RequirePermission>);
    expect(screen.getByRole('heading', { name: 'Tenants' })).toBeInTheDocument();
  });

  it('offers a way back when the app supplies one', async () => {
    let went = false;
    wrap(holder,
      <RequirePermission perm="media:write" onBack={() => { went = true; }}>
        <h1>Media</h1>
      </RequirePermission>);
    await userEvent.click(screen.getByRole('button', { name: 'Go back' }));
    expect(went).toBe(true);
  });
});

describe('PermissionTooltip', () => {
  it('returns the control untouched for a holder — no wrapper in the way of layout', () => {
    const { container } = wrap(holder,
      <PermissionTooltip perm="media:read" reason="Needs media:write">
        <Button>Upload</Button>
      </PermissionTooltip>);
    expect(container.querySelector('span')).toBeNull();
    expect(screen.getByRole('button', { name: 'Upload' })).toBeInTheDocument();
  });

  it('wraps a refused control, so a tooltip has something to hang off', () => {
    // A disabled button emits no pointer events, so a tooltip attached to the button itself
    // would never open — which is exactly the case where the explanation matters. Asserting
    // the wrapper rather than driving the hover: whether Radix opens on pointerenter is
    // Radix's business, and its pointer sequence is not reliable under jsdom.
    wrap(holder,
      <PermissionTooltip perm="media:write" reason="Needs the media:write permission">
        <Button disabled>Upload</Button>
      </PermissionTooltip>);
    const wrapper = screen.getByRole('button', { name: 'Upload' }).parentElement!;
    expect(wrapper.tagName).toBe('SPAN');
    expect(wrapper).toHaveAttribute('data-state');
  });

  it('still shows the control, rather than hiding it', () => {
    wrap(holder,
      <PermissionTooltip perm="media:write" reason="Needs media:write">
        <Button disabled>Upload</Button>
      </PermissionTooltip>);
    expect(screen.getByRole('button', { name: 'Upload' })).toBeInTheDocument();
  });
});
