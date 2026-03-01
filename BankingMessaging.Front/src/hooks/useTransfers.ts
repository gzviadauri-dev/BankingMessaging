import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/bankingApi'
import type { InitiateTransferRequest } from '@/types'

export const useTransfers = () =>
  useQuery({
    queryKey: ['transfers'],
    queryFn: () => api.getTransfers(),
    refetchInterval: 3000,
    staleTime: 1000,
  })

export const useInitiateTransfer = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: InitiateTransferRequest) => api.initiateTransfer(body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['transfers'] })
      qc.invalidateQueries({ queryKey: ['accounts'] })
    },
  })
}
