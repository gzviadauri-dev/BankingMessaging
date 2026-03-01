import type { Account, InitiateTransferRequest, Transfer, TransferResponse } from '@/types'

const BASE = '/api'

async function handleResponse<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const err = await res.json().catch(() => ({}))
    throw new Error((err as { detail?: string; message?: string }).detail
      ?? (err as { message?: string }).message
      ?? `HTTP ${res.status}`)
  }
  return res.json() as Promise<T>
}

export const api = {
  getAccounts: (): Promise<Account[]> =>
    fetch(`${BASE}/accounts`).then(r => handleResponse<Account[]>(r)),

  getAccount: (id: string): Promise<Account> =>
    fetch(`${BASE}/accounts/${id}`).then(r => handleResponse<Account>(r)),

  initiateTransfer: (body: InitiateTransferRequest): Promise<TransferResponse> =>
    fetch(`${BASE}/transfers`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }).then(r => handleResponse<TransferResponse>(r)),

  getTransfers: (status?: string): Promise<Transfer[]> =>
    fetch(`${BASE}/transfers${status ? `?status=${status}` : ''}`).then(r =>
      handleResponse<Transfer[]>(r),
    ),

  getTransfer: (id: string): Promise<Transfer> =>
    fetch(`${BASE}/transfers/${id}`).then(r => handleResponse<Transfer>(r)),

  healthCheck: (): Promise<{ status: string }> =>
    fetch(`${BASE}/health`).then(r => handleResponse<{ status: string }>(r)),
}
