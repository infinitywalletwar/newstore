import { ShipmentStatus } from '../api/models';

/** The four steps, in the order a shipment goes through them. */
export const SHIPMENT_STEPS: readonly ShipmentStatus[] = ['Preparing', 'PickedUp', 'InTransit', 'Delivered'];

export const STATUS_LABELS: Record<ShipmentStatus, string> = {
  Preparing: 'Preparing',
  PickedUp: 'Picked up',
  InTransit: 'In transit',
  Delivered: 'Delivered',
};

export type StepState = 'done' | 'current' | 'todo';

/** A step is `done` before the status, `current` at it, `todo` after it. At `Delivered` every step is `done`. */
export function stepState(step: ShipmentStatus, status: ShipmentStatus): StepState {
  const index = SHIPMENT_STEPS.indexOf(step);
  const at = SHIPMENT_STEPS.indexOf(status);
  if (status === 'Delivered' || index < at) {
    return 'done';
  }
  return index === at ? 'current' : 'todo';
}
