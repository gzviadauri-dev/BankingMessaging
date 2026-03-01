import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/bankingApi'

export const useAccounts = () =>
  useQuery({
    queryKey: ['accounts'],
    queryFn: api.getAccounts,
    refetchInterval: 5000,
    staleTime: 2000,
  })

export const useApiHealth = () =>
  useQuery({
    queryKey: ['health'],
    queryFn: api.healthCheck,
    refetchInterval: 10000,
    staleTime: 5000,
    retry: 1,
  })
