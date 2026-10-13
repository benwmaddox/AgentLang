# Regenerates rails-html-escape-vectors.json from the pinned Rails reference image (run in WSL).
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
docker run --rm --entrypoint "" -v "$here":/v \
  once-campfire-verification:reference-90b3300 \
  bundle exec ruby /v/rails_escape_vectors.rb /v/rails-html-escape-vectors.json
