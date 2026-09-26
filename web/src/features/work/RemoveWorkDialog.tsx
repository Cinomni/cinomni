import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router'
import { catalogApi } from '@/api/endpoints'
import type { Work } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { Alert } from '@/ui/Alert'
import { Button } from '@/ui/Button'
import { Checkbox } from '@/ui/Checkbox'
import { Modal } from '@/ui/Modal'

/**
 * Removes a movie or a series from the catalog. What the removal does to the rest of the installation is
 * the backend's business: it stops monitoring, cancels searches and drops torrents on its own. The one
 * choice left to the person is whether the files on disk go too, and it starts unticked — a file deleted
 * by a stray click is the one thing here that cannot be put back.
 */
export function RemoveWorkDialog({
  work,
  open,
  onClose,
}: {
  work: Pick<Work, 'id' | 'title' | 'kind'>
  open: boolean
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [deleteFiles, setDeleteFiles] = useState(false)
  const isSeries = work.kind === 'Series'

  const remove = useMutation({
    mutationFn: () => catalogApi.remove(work.id, deleteFiles),
    onSuccess: async () => {
      queryClient.removeQueries({ queryKey: ['work', work.id] })
      await queryClient.invalidateQueries({ queryKey: ['works'] })
      navigate(isSeries ? '/series' : '/movies', { replace: true })
    },
  })

  function close() {
    setDeleteFiles(false)
    remove.reset()
    onClose()
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    remove.mutate()
  }

  return (
    <Modal open={open} onClose={close} title={`Remove ${work.title}`}>
      <form onSubmit={onSubmit} className="flex flex-col gap-4">
        <p className="text-sm text-muted">
          {isSeries ? 'The show' : 'The movie'} leaves the library and nothing more is searched or downloaded for
          it. Downloads in progress are stopped.
        </p>
        <Checkbox
          label="Also delete the files from disk"
          hint="The imported files and the downloads. This cannot be undone."
          checked={deleteFiles}
          onChange={(event) => setDeleteFiles(event.target.checked)}
        />

        {remove.isError && <Alert tone="danger">{errorMessage(remove.error, 'Could not remove this title.')}</Alert>}

        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={close}>
            Cancel
          </Button>
          <Button type="submit" variant="danger" loading={remove.isPending}>
            {deleteFiles ? 'Remove and delete files' : 'Remove'}
          </Button>
        </div>
      </form>
    </Modal>
  )
}
