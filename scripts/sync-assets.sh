#!/usr/bin/env bash
# sync-assets.sh — move Foliant's PRIVATE, gitignored assets between machines.
#
# WHY THIS EXISTS: git carries source, not the heavy/private assets. The trained
# models (kv4 LiLT ≈519M) and the test corpora (TD-41 ≈182M) are gitignored on
# purpose — they are private and too large — so a fresh clone or a `git reset
# --hard` has NONE of them, and a machine wipe loses them with nothing to
# restore from. This script is the sanctioned way to move them, machine → machine,
# over your own network. NOTHING HERE TOUCHES GITHUB.
#
# MODEL: one machine is AUTHORITATIVE (the Mac). Run this there.
#   push          seed / refresh a remote box with the assets needed to run
#   pull-results  bring result dumps (out/*.csv) back from the box to analyse
#   promote-model bring a RETRAINED model box → Mac (explicit; overwrites authoritative)
#   verify        show what each side has (paths + sizes), no transfer
#
# Source code still travels via git (`git push`/`git pull`). This is ONLY the
# gitignored assets. Keep data/ and models/ in .gitignore — that is the privacy guard.
set -euo pipefail

# ── config (override via env, no need to edit) ────────────────────────────────
REMOTE="${FOLIANT_REMOTE:-ssuresh@subuntu}"                 # user@host of the box
REMOTE_DIR="${FOLIANT_REMOTE_DIR:-~/foliant}"               # repo root on the box (~ expanded remotely)
LOCAL_DIR="${FOLIANT_LOCAL_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"  # repo root here

# Private, gitignored assets required to RUN (authoritative copy = this machine).
# Each entry is a path relative to the repo root; each is a self-contained dir.
ASSETS=(
  "models/form-kv-lilt"             # trained LiLT kv4 (PROMOTED) — the live model
  "models/form-kv-lilt.kv3-backup"  # prior kv3 snapshot (rollback safety)
  "data/Test-Data-41"               # scanned holdout corpus (Gate 3, 165 scan pairs)
  "data/Test-Data-21"               # promotion-gate corpus (922 pages)
)
# NOTE: public ONNX models (layout/ocr/table) are NOT listed — they are fetched
# reproducibly by scripts/download-models.sh. Run that on a new box instead of
# copying them. Add them here only if you want to avoid the re-download.

RSYNC_BASE=(-avh --exclude '.DS_Store' --exclude 'bin/' --exclude 'obj/')
DELETE=()            # set to (--delete) for an exact mirror; toggled by --mirror
DRY=()               # set to (--dry-run) by --dry-run
PROGRESS=(--progress)

usage() {
  cat >&2 <<EOF
sync-assets.sh — private (non-git) asset sync for Foliant

USAGE:
  scripts/sync-assets.sh <command> [--mirror] [--dry-run]

COMMANDS:
  push                 Mac → box: copy every asset in the manifest to the box.
  pull-results         box → Mac: copy out/*.csv (result dumps) back for analysis.
  promote-model NAME   box → Mac: copy models/NAME back (overwrites authoritative).
                       Use after retraining on the GPU box to promote a model.
  verify               Show asset paths + sizes on both machines. No transfer.

OPTIONS:
  --mirror    Use rsync --delete so the destination EXACTLY matches the source
              (removes stale files — this is what prevents "old model mixed with
              new" bugs). Recommended for push. Off by default (additive/safe).
  --dry-run   Show what would transfer, change nothing.

CONFIG (env overrides):
  FOLIANT_REMOTE      user@host of the box     (default: $REMOTE)
  FOLIANT_REMOTE_DIR  repo root on the box     (default: ~/foliant)
  FOLIANT_LOCAL_DIR   repo root here           (default: auto-detected)

EXAMPLES:
  scripts/sync-assets.sh push --mirror          # seed/refresh the box, exact copy
  scripts/sync-assets.sh push --dry-run         # preview first
  scripts/sync-assets.sh pull-results           # grab out/*.csv back to the Mac
  scripts/sync-assets.sh promote-model form-kv-lilt   # box's retrained model → Mac
  FOLIANT_REMOTE=ssuresh@10.0.0.7 scripts/sync-assets.sh push
EOF
  exit "${1:-2}"
}

log()  { printf '\033[1;34m▶ %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m⚠ %s\033[0m\n' "$*" >&2; }

# ── parse flags (command first, then options in any order) ────────────────────
[[ $# -ge 1 ]] || usage 2
CMD="$1"; shift
PROMOTE_NAME=""
if [[ "$CMD" == "promote-model" ]]; then
  [[ $# -ge 1 && "${1:0:2}" != "--" ]] || { warn "promote-model needs a model dir name (e.g. form-kv-lilt)"; usage 2; }
  PROMOTE_NAME="$1"; shift
fi
while [[ $# -gt 0 ]]; do
  case "$1" in
    --mirror)  DELETE=(--delete) ;;
    --dry-run) DRY=(--dry-run) ;;
    -h|--help) usage 0 ;;
    *) warn "unknown option: $1"; usage 2 ;;
  esac
  shift
done

RSYNC=(rsync "${RSYNC_BASE[@]}" "${PROGRESS[@]}" "${DELETE[@]}" "${DRY[@]}")

do_push() {
  log "PUSH  ${LOCAL_DIR}  →  ${REMOTE}:${REMOTE_DIR}   (mirror=${DELETE:+on}${DELETE:-off}, dry=${DRY:+yes}${DRY:-no})"
  for a in "${ASSETS[@]}"; do
    local src="${LOCAL_DIR}/${a}"
    if [[ ! -e "$src" ]]; then warn "skip (missing locally): $a"; continue; fi
    local parent; parent="$(dirname "$a")"
    log "  $a"
    # copy the asset DIR into its parent on the remote (creates parent if needed)
    ssh "$REMOTE" "mkdir -p ${REMOTE_DIR}/${parent}"
    "${RSYNC[@]}" "$src" "${REMOTE}:${REMOTE_DIR}/${parent}/"
  done
  log "push complete."
}

do_pull_results() {
  local dst="${LOCAL_DIR}/out"
  mkdir -p "$dst"
  log "PULL RESULTS  ${REMOTE}:${REMOTE_DIR}/out/*.csv  →  ${dst}/"
  # additive (never --delete on results); brings new dumps back for analysis
  rsync "${RSYNC_BASE[@]}" "${PROGRESS[@]}" "${DRY[@]}" \
    --include '*.csv' --include '*/' --exclude '*' \
    "${REMOTE}:${REMOTE_DIR}/out/" "${dst}/"
  log "results in ${dst}/ (list newest):"
  ls -lt "$dst"/*.csv 2>/dev/null | head || warn "no CSVs found"
}

do_promote_model() {
  local a="models/${PROMOTE_NAME}"
  local dst="${LOCAL_DIR}/${a}"
  warn "PROMOTE: this OVERWRITES the authoritative ${a} on THIS machine with the box's copy."
  warn "Tip: snapshot first →  mv ${a} ${a}.$(date +%Y%m%d)-backup"
  log "PULL MODEL  ${REMOTE}:${REMOTE_DIR}/${a}  →  ${dst}"
  mkdir -p "$(dirname "$dst")"
  # mirror so the promoted model is EXACT (no stale files left behind)
  rsync "${RSYNC_BASE[@]}" "${PROGRESS[@]}" --delete "${DRY[@]}" \
    "${REMOTE}:${REMOTE_DIR}/${a}" "$(dirname "$dst")/"
  log "promoted ${a}. Commit nothing — it's gitignored. Re-run push to fan out to other boxes."
}

human_size() { du -sh "$1" 2>/dev/null | cut -f1; }

do_verify() {
  log "VERIFY assets"
  printf '%-34s %10s   %10s\n' "asset" "local" "remote(${REMOTE})"
  printf '%-34s %10s   %10s\n' "-----" "-----" "------"
  for a in "${ASSETS[@]}"; do
    local ls_="—" rs_="—"
    [[ -e "${LOCAL_DIR}/${a}" ]] && ls_="$(human_size "${LOCAL_DIR}/${a}")"
    rs_="$(ssh "$REMOTE" "du -sh ${REMOTE_DIR}/${a} 2>/dev/null | cut -f1" 2>/dev/null || true)"
    [[ -z "$rs_" ]] && rs_="—"
    printf '%-34s %10s   %10s\n' "$a" "$ls_" "$rs_"
  done
}

case "$CMD" in
  push)          do_push ;;
  pull-results)  do_pull_results ;;
  promote-model) do_promote_model ;;
  verify)        do_verify ;;
  -h|--help)     usage 0 ;;
  *) warn "unknown command: $CMD"; usage 2 ;;
esac
