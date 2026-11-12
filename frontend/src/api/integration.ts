import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api } from './client'

/*
  The systems that call the integration API (ADR-024), as an administrator registers them.

  Mirrors Sqm.Contracts.Integration. A client's secret appears in exactly two answers - the one
  that registers it and the one that rotates it - and in no read: PI keeps only its hash. So the
  page shows a secret once, from the answer it came in, and nothing here caches it.
*/

/** A registered system. Times are ISO 8601, UTC. */
export interface IntegrationClient {
  id: number
  /** What the system sends as client_id: lower-case letters, digits and hyphens. */
  clientId: string
  displayName: string
  description: string
  scopes: string[]
  isActive: boolean
  secretSetAt: string
  /** When it last obtained a token: how an administrator tells a live integration from a dead one. */
  lastTokenAt: string | null
  createdAt: string
  createdBy: string
  updatedAt: string
  updatedBy: string
}

/** A scope an administrator may grant, and what it allows. */
export interface IntegrationScope {
  scope: string
  description: string
}

export interface IntegrationClientList {
  clients: IntegrationClient[]
  scopes: IntegrationScope[]
}

/** A client with a secret just issued. The only time the secret is seen. */
export interface IssuedSecret {
  client: IntegrationClient
  clientSecret: string
}

export interface NewIntegrationClient {
  clientId: string
  displayName: string
  description: string
  scopes: string[]
}

export interface IntegrationClientChange {
  displayName: string
  description: string
  scopes: string[]
  /** False deactivates it, and every token it holds stops working. */
  isActive: boolean
}

const CLIENTS_KEY = ['integration', 'clients'] as const

export function useIntegrationClients() {
  return useQuery({
    queryKey: CLIENTS_KEY,
    queryFn: ({ signal }) => api.get<IntegrationClientList>('/api/v1/integration/clients/', undefined, signal),
  })
}

export function useCreateIntegrationClient() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: NewIntegrationClient) => api.post<IssuedSecret>('/api/v1/integration/clients/', input),
    onSuccess: () => client.invalidateQueries({ queryKey: CLIENTS_KEY }),
  })
}

export function useUpdateIntegrationClient() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, change }: { id: number; change: IntegrationClientChange }) =>
      api.put<IntegrationClient>(`/api/v1/integration/clients/${id}`, change),
    onSuccess: () => client.invalidateQueries({ queryKey: CLIENTS_KEY }),
  })
}

export function useRotateIntegrationSecret() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.post<IssuedSecret>(`/api/v1/integration/clients/${id}/secret`, {}),
    onSuccess: () => client.invalidateQueries({ queryKey: CLIENTS_KEY }),
  })
}
