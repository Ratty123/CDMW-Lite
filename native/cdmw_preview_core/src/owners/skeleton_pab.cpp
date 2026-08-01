
// The rig a skinned PAC follows: the .pab that holds it, and the palette that turns the mesh's
// opaque influence slots into bones of that rig.
//
// A .pab is a 22-byte header, a uint16 bone count at 0x14, and then one fixed 305-byte record per
// bone: a 4-byte name hash, a length-prefixed ASCII name, a 4-byte int32 parent (-1 for a root),
// four 64-byte matrices -- bind, inverse bind, and a copy of each -- then 12 bytes of scale, a
// 16-byte rotation quaternion in xyzw, and 12 bytes of position.
//
// Nothing here guesses. A record that does not fit the fixed layout yields no skeleton rather
// than a scanned approximation of one, because a bone recovered by scanning would be named and
// placed on evidence the file did not give, and a mesh bound to it deforms into nonsense.

constexpr size_t kPabHeaderSize = 0x16;
constexpr size_t kPabBoneCountOffset = 0x14;
constexpr size_t kPabBoneFixedSize = 305;
constexpr size_t kPabMaximumNameLength = 255;

// A bone index has to survive a round trip through the u16 skin rows, which spend 0xFFFF on
// "no influence". No rig in the archive comes anywhere near this.
constexpr size_t kMaximumSkeletonBones = 0xFFFE;

static std::array<float, 16> read_pab_matrix(const std::vector<char>& data, size_t offset) {
    std::array<float, 16> matrix{};
    for (size_t element = 0; element < matrix.size(); ++element) {
        matrix[element] = read_f32(data, offset + element * 4u);
    }
    return matrix;
}

static std::vector<NativeBone> parse_pab_skeleton(const std::vector<char>& data) {
    if (data.size() < kPabHeaderSize || std::string(data.data(), data.data() + 4) != "PAR ") {
        throw std::runtime_error("selected PAB is missing a PAR header");
    }
    const size_t bone_count = read_u16(data, kPabBoneCountOffset);
    if (bone_count == 0) return {};
    if (bone_count > kMaximumSkeletonBones) {
        throw std::runtime_error("PAB bone count is outside the supported range");
    }
    std::vector<NativeBone> bones;
    bones.reserve(bone_count);
    size_t offset = kPabHeaderSize;
    for (size_t index = 0; index < bone_count; ++index) {
        if (offset + kPabBoneFixedSize > data.size()) {
            throw std::runtime_error("PAB bone record is truncated");
        }
        NativeBone bone;
        bone.name_hash = read_u32(data, offset);
        offset += 4;
        const size_t name_length = static_cast<unsigned char>(data[offset]);
        offset += 1;
        if (name_length > kPabMaximumNameLength || offset + name_length + 4 > data.size()) {
            throw std::runtime_error("PAB bone name is truncated");
        }
        bone.name.assign(data.data() + offset, name_length);
        offset += name_length;
        bone.parent_index = static_cast<std::int32_t>(read_u32(data, offset));
        offset += 4;
        bone.bind_matrix = read_pab_matrix(data, offset);
        offset += 64;
        bone.inverse_bind_matrix = read_pab_matrix(data, offset);
        offset += 64;
        // Two further copies of the same two matrices, part of the record's stride and used by
        // nothing here.
        offset += 128;
        bone.scale = {read_f32(data, offset), read_f32(data, offset + 4), read_f32(data, offset + 8)};
        offset += 12;
        bone.rotation = {
            read_f32(data, offset), read_f32(data, offset + 4),
            read_f32(data, offset + 8), read_f32(data, offset + 12)};
        offset += 16;
        bone.position = {read_f32(data, offset), read_f32(data, offset + 4), read_f32(data, offset + 8)};
        offset += 12;
        if (bone.parent_index < -1 || bone.parent_index >= static_cast<std::int32_t>(bone_count)) {
            throw std::runtime_error("PAB bone names a parent outside the skeleton");
        }
        bones.push_back(std::move(bone));
    }
    return bones;
}

// Bone-hash palette tables in a PAC, longest first.
//
// A skinned PAC carries its own palette: a u16 count followed by that many u32 .pab bone-name
// hashes. A vertex's influence slot indexes this table, which is what turns an opaque slot into a
// named bone of the rig.
//
// Several byte runs satisfy the shape -- 2,226 of them in one body mesh -- so every plausible
// table is returned and the caller keeps the one that actually resolves against a skeleton.
// Choosing here instead would silently mis-name bones.
static std::vector<std::vector<std::uint32_t>> pac_bone_palette_candidates(
    const std::vector<char>& data,
    size_t search_limit,
    size_t minimum_entries = 8,
    size_t maximum_entries = 512,
    size_t maximum_candidates = 32768
) {
    std::vector<std::vector<std::uint32_t>> found;
    if (data.size() < 6) return found;
    const size_t limit = std::min(search_limit, data.size() - 6);
    for (size_t offset = 16; offset < limit; ++offset) {
        const size_t count = read_u16(data, offset);
        if (count < minimum_entries || count > maximum_entries) continue;
        if (offset + 2 + count * 4 > data.size()) continue;
        // Real hashes are large and unique; counts, offsets and index runs are neither. Testing
        // the first value before unpacking the rest is what keeps a whole-file scan affordable.
        if (read_u32(data, offset + 2) < 0x10000u) continue;
        std::vector<std::uint32_t> values;
        values.reserve(count);
        bool plausible = true;
        for (size_t entry = 0; entry < count; ++entry) {
            const std::uint32_t value = read_u32(data, offset + 2 + entry * 4);
            if (value < 0x10000u) {
                plausible = false;
                break;
            }
            values.push_back(value);
        }
        if (!plausible) continue;
        std::vector<std::uint32_t> sorted = values;
        std::sort(sorted.begin(), sorted.end());
        if (std::adjacent_find(sorted.begin(), sorted.end()) != sorted.end()) continue;
        found.push_back(std::move(values));
        if (found.size() >= maximum_candidates) break;
    }
    std::stable_sort(found.begin(), found.end(), [](const auto& a, const auto& b) {
        return a.size() > b.size();
    });
    return found;
}

// Map the PAC's influence slots onto bone indices in `bones`, or return nothing.
//
// A candidate has to resolve completely: every hash a bone this rig actually has. That is a strong
// enough filter to be unambiguous -- of the thousands of tables that merely look like a palette in
// a body mesh, exactly one resolves -- so a mismatched or missing skeleton yields nothing rather
// than a wrong palette.
//
// The cheap scan of the file's head runs first, because that is where a character body keeps its
// palette. Armour keeps it further in: a coat, a boot and a helmet all come back empty from the
// head alone and every one of them resolves once the whole file is searched.
static std::vector<std::int32_t> resolve_pac_bone_palette(
    const std::vector<char>& data,
    const std::vector<NativeBone>& bones
) {
    if (bones.empty()) return {};
    std::unordered_map<std::uint32_t, std::int32_t> by_hash;
    by_hash.reserve(bones.size() * 2u);
    for (size_t index = 0; index < bones.size(); ++index) {
        by_hash.emplace(bones[index].name_hash, static_cast<std::int32_t>(index));
    }
    for (const size_t search_limit : {static_cast<size_t>(4096), data.size()}) {
        std::vector<std::int32_t> best;
        for (const std::vector<std::uint32_t>& candidate : pac_bone_palette_candidates(data, search_limit)) {
            if (candidate.size() <= best.size()) continue;
            std::vector<std::int32_t> resolved;
            resolved.reserve(candidate.size());
            bool complete = true;
            for (const std::uint32_t value : candidate) {
                auto found = by_hash.find(value);
                if (found == by_hash.end()) {
                    complete = false;
                    break;
                }
                resolved.push_back(found->second);
            }
            if (complete) best = std::move(resolved);
        }
        if (!best.empty()) return best;
        if (search_limit >= data.size()) break;
    }
    return {};
}

// The .pab basenames a PAC may be rigged to, most specific first.
//
// The rule is the file's own name, then the family its name states, then the rig its directory
// states -- and the directory is what usually settles it. cd_phw_00_nude_00_0001_damian.pac is
// served by phw_01.pab, which is named after the folder 2_phw and by nothing in the mesh's own
// filename. Matching by directory alone is the mistake that looks obvious and is not.
static std::vector<std::string> iter_pab_candidate_basenames(const std::string& pac_path) {
    static const std::regex rig_family_pattern("^(?:cd_)?([a-z0-9]+)_([0-9]{2})(?:_|$)");
    static const std::regex class_directory_pattern("^[0-9]+_([a-z]{2,5})$");
    static const std::regex monster_directory_pattern("^cd_m[0-9]{4}_");

    const std::string normalized = lower_copy(pac_path);
    const std::string stem = stem_from_path(normalized);
    std::vector<std::string> ordered;
    if (stem.empty()) return ordered;
    std::set<std::string> seen;
    const auto append = [&](std::string candidate) {
        if (candidate.empty()) return;
        if (candidate.size() < 4 || candidate.compare(candidate.size() - 4, 4, ".pab") != 0) {
            candidate += ".pab";
        }
        if (seen.insert(candidate).second) ordered.push_back(candidate);
    };

    append(stem);

    std::vector<std::string> tokens;
    for (size_t start = 0; start <= stem.size();) {
        const size_t next = stem.find('_', start);
        const size_t end = next == std::string::npos ? stem.size() : next;
        if (end > start) tokens.push_back(stem.substr(start, end - start));
        if (next == std::string::npos) break;
        start = next + 1;
    }
    if (tokens.size() >= 3 && tokens[0] == "cd") {
        append(tokens[0] + "_" + tokens[1] + "_" + tokens[2]);
        append(tokens[1] + "_" + tokens[2]);
    }
    std::smatch family;
    if (std::regex_search(stem, family, rig_family_pattern)) {
        const std::string family_name = family[1].str() + "_" + family[2].str();
        append(family_name);
        append("cd_" + family_name);
    }

    for (size_t start = 0; start < normalized.size();) {
        const size_t next = normalized.find('/', start);
        const std::string part = normalized.substr(start, (next == std::string::npos ? normalized.size() : next) - start);
        if (!part.empty()) {
            std::smatch klass;
            if (std::regex_search(part, klass, class_directory_pattern)) append(klass[1].str() + "_01");
            if (std::regex_search(part, monster_directory_pattern)) append(part);
        }
        if (next == std::string::npos) break;
        start = next + 1;
    }
    if (normalized.find("character/model/1_pc/") != std::string::npos) append("identityskeleton");
    return ordered;
}

// How a PAC's vertices are bound, read off the records themselves.
enum class PacSkinBinding {
    None,
    Rigid,
    Smooth,
};

// Rigidly bound meshes -- props, accessories, vehicles -- put the whole weight of 255 on one
// influence and leave every slot at zero. cd_phm_00_bag_0050.pac and
// cd_m0027_00_plundertank_00_0002.pac are both entirely this, across 180,325 and 178,392
// vertices. Those files carry no bone hash anywhere, so there is no palette to look for; which
// bone the mesh follows is recorded outside the mesh file. Telling the two styles apart here is
// what keeps a rigid mesh from being searched for a rig it cannot name, and from being bound to
// palette entry zero if one were found anyway.
static PacSkinBinding classify_pac_skin(const std::vector<NativeSubmesh>& meshes) {
    bool saw_any = false;
    for (const NativeSubmesh& mesh : meshes) {
        if (mesh.source_prefab_component || mesh.export_skin.empty()) continue;
        saw_any = true;
        for (const NativeSkinInfluence& skin : mesh.export_skin) {
            for (int influence = 0; influence < kPacSkinInfluences; ++influence) {
                const bool weighted = skin.weights[static_cast<size_t>(influence)] != 0;
                if (skin.slots[static_cast<size_t>(influence)] != 0) return PacSkinBinding::Smooth;
                if (influence > 0 && weighted) return PacSkinBinding::Smooth;
            }
        }
    }
    return saw_any ? PacSkinBinding::Rigid : PacSkinBinding::None;
}

static NativePackageSkeleton resolve_native_package_skeleton(
    const EntryJob& job,
    const PamtIndex& index,
    const std::vector<char>& data,
    const std::vector<NativeSubmesh>& meshes
) {
    NativePackageSkeleton skeleton;
    switch (classify_pac_skin(meshes)) {
        case PacSkinBinding::None:
            skeleton.status = "not_skinned";
            skeleton.note = "the vertex layout carries no skin field";
            return skeleton;
        case PacSkinBinding::Rigid:
            skeleton.status = "rigid";
            skeleton.note =
                "every vertex is a single influence at full weight and every slot is zero; "
                "the bone a rigidly bound mesh follows is not recorded in the mesh file";
            return skeleton;
        case PacSkinBinding::Smooth:
            break;
    }

    std::vector<std::string> attempted;
    // Each candidate costs a decode and a palette scan, so the search is bounded. The rig that
    // serves a mesh is among the first few names by construction.
    constexpr size_t kMaximumSkeletonsTried = 8;
    std::set<std::string> tried_paths;
    for (const std::string& basename : iter_pab_candidate_basenames(job.path)) {
        for (const ArchiveEntryRef& ref : lookup_basename_candidates_across_package(job, index, basename, 4)) {
            if (tried_paths.size() >= kMaximumSkeletonsTried) break;
            if (!tried_paths.insert(lower_copy(ref.path)).second) continue;
            try {
                const std::vector<char> pab = read_archive_ref_decoded_bytes(ref);
                std::vector<NativeBone> bones = parse_pab_skeleton(pab);
                if (bones.empty()) continue;
                std::vector<std::int32_t> palette = resolve_pac_bone_palette(data, bones);
                if (palette.empty()) {
                    attempted.push_back(ref.path + " (" + std::to_string(bones.size()) + " bones, no palette resolved)");
                    continue;
                }
                skeleton.status = "rigged";
                skeleton.source_path = ref.path;
                skeleton.bones = std::move(bones);
                skeleton.palette = std::move(palette);
                skeleton.note = "palette of " + std::to_string(skeleton.palette.size())
                    + " entries resolved against " + std::to_string(skeleton.bones.size()) + " bones";
                return skeleton;
            } catch (const std::exception& exc) {
                attempted.push_back(ref.path + " (" + exc.what() + ")");
            }
        }
        if (tried_paths.size() >= kMaximumSkeletonsTried) break;
    }

    skeleton.status = "palette_unresolved";
    if (attempted.empty()) {
        skeleton.note = "the mesh is smooth-skinned but no candidate .pab was found in the package";
    } else {
        std::ostringstream note;
        note << "the mesh is smooth-skinned but no candidate .pab resolved its palette:";
        for (const std::string& entry : attempted) note << " " << entry << ";";
        skeleton.note = note.str();
    }
    return skeleton;
}
